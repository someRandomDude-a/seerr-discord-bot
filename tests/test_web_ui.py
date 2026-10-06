import asyncio
import base64
import tempfile
import time
import unittest
from pathlib import Path
from unittest.mock import MagicMock, patch

from aiohttp.test_utils import TestClient, TestServer

from bot import SeerrBot
from media_bot.activity import ActivityAPI, item_reference, public_result
from media_bot.config import Config
from media_bot.linking import QuickConnect
from media_bot.security import UserError
from media_bot.service import MediaService
from media_bot.ui import DashboardView, ItemsView, PreferencesView
from media_bot.webhooks import WebhookServer


class WebhookTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.config = Config('http://seerr', 'secret', 'token', 'unused.db', webhook_secret='x' * 40)
        self.service = MagicMock()
        self.service.config = self.config
        self.service.arr = {'radarr': MagicMock()}
        self.event = asyncio.Event()
        self.server = WebhookServer(self.service, self.event)
        self.client = TestClient(TestServer(self.server.application()))
        await self.client.start_server()

    async def asyncTearDown(self):
        await self.client.close()

    async def test_no_browser_registration_routes(self):
        for path in ('/', '/link/token', '/assets/theme.css'):
            response = await self.client.get(path)
            self.assertEqual(response.status, 404)

    async def test_unauthenticated_webhook_rejected(self):
        response = await self.client.post('/webhooks/radarr', json={'eventType': 'Download'})
        self.assertEqual(response.status, 401)
        self.assertFalse(self.event.is_set())

    async def test_basic_and_bearer_webhooks_only_signal_sync(self):
        basic = base64.b64encode(('bot:' + self.config.webhook_secret).encode()).decode()
        for header in ('Basic ' + basic, 'Bearer ' + self.config.webhook_secret):
            self.event.clear()
            response = await self.client.post('/webhooks/radarr', json={'eventType': 'Download', 'movie': {'title': '@everyone untrusted'}}, headers={'Authorization': header})
            self.assertEqual(response.status, 200)
            self.assertTrue(self.event.is_set())

    async def test_test_notification_does_not_wake_sync(self):
        response = await self.client.post('/webhooks/radarr', json={'eventType': 'Test'}, headers={'Authorization': 'Bearer ' + self.config.webhook_secret})
        self.assertEqual(response.status, 200)
        self.assertFalse(self.event.is_set())

    async def test_unknown_source_and_bad_payload_rejected(self):
        auth = {'Authorization': 'Bearer ' + self.config.webhook_secret}
        response = await self.client.post('/webhooks/lidarr', json={'eventType': 'Download'}, headers=auth)
        self.assertEqual(response.status, 404)
        response = await self.client.post('/webhooks/radarr', json=['invalid'], headers=auth)
        self.assertEqual(response.status, 400)


class LinkingTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.service = MagicMock()
        self.service.config = Config('http://seerr', 'key', 'token', 'unused.db')
        self.service.offload = MediaService.offload
        self.service.lock = asyncio.Lock()
        self.service.link_authenticated.return_value = 'Viewer'
        self.manager = QuickConnect(self.service)
        self.client = MagicMock()
        self.client._request.return_value = {'code': '123456', 'secret': 'a' * 32}
        self.patcher = patch('media_bot.linking.SeerrAPI', return_value=self.client)
        self.factory = self.patcher.start()

    async def asyncTearDown(self):
        await self.manager.close()
        self.patcher.stop()

    async def test_linking_requires_approval_and_never_uses_admin_key(self):
        session = await self.manager.begin(42)
        self.factory.assert_called_once_with('http://seerr', timeout=15)
        self.client._request.return_value = {'authenticated': False}
        self.assertEqual(await self.manager.check(session), 'pending')
        self.service.link_authenticated.assert_not_called()
        self.client._request.side_effect = [{'authenticated': True}, {}]
        self.assertEqual(await self.manager.check(session), 'Viewer')
        self.service.link_authenticated.assert_called_once_with(42, self.client)
        self.assertEqual(session.state, 'linked')
        await self.manager.close_client(session)
        self.client.logout_user.assert_called_once()

    async def test_cancelled_and_replaced_sessions_cannot_link(self):
        old = await self.manager.begin(42)
        new = await self.manager.begin(42)
        self.assertEqual(old.state, 'cancelled')
        self.assertFalse(await self.manager.cancel(42, old.generation))
        await self.manager.check(old)
        self.service.link_authenticated.assert_not_called()
        self.assertTrue(await self.manager.cancel(42, new.generation))

    async def test_expired_code_cannot_link(self):
        session = await self.manager.begin(42)
        session.expires = time.monotonic() - 1
        self.assertEqual(await self.manager.check(session), 'expired')
        self.service.link_authenticated.assert_not_called()

    async def test_poll_updates_private_discord_response(self):
        session = await self.manager.begin(42)
        self.client._request.side_effect = [{'authenticated': True}, {}]
        from unittest.mock import AsyncMock
        interaction = MagicMock()
        interaction.edit_original_response = AsyncMock()
        self.service.config.link_poll_interval = 0.001
        self.manager.attach(session, interaction)
        await session.task
        self.assertEqual(session.state, 'linked')
        interaction.edit_original_response.assert_awaited_once()
        self.assertNotIn(42, self.manager.sessions)


class InterfaceTests(unittest.IsolatedAsyncioTestCase):
    async def test_bot_registers_commands_and_views_fit_discord_limits(self):
        with tempfile.TemporaryDirectory() as temp:
            config = Config('http://seerr', 'secret', 'token', str(Path(temp) / 'cache.db'), devices={'Home': 'https://jellyfin.example'})
            bot = SeerrBot(config)
            try:
                names = {command.name for command in bot.tree.get_commands()}
                self.assertIn('deletions', names)
                self.assertIn('activity', names)
                self.assertNotIn('review', names)
                self.assertFalse(bot.intents.message_content)
                bot.service.store.link(42, 7, 'Viewer')
                for view in (DashboardView(bot, 42), PreferencesView(bot, 42)):
                    self.assertLessEqual(len(view.children), 25)
                    for row in range(5):
                        self.assertLessEqual(sum(child.width for child in view.children if child._rendered_row == row), 5)
                view = ItemsView(bot, 42, 'requests', (False,))
                view.items = [{'id': n, 'kind': 'movie', 'external_id': str(n), 'title': 'A title', 'subtitle': 'Available'} for n in range(100)]
                view.rebuild()
                self.assertEqual(len(view.render().fields), 5)
                self.assertLessEqual(len(view.render()), 6000)
            finally:
                bot.service.api.close()


class ActivityTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.bot = MagicMock()
        self.bot.config = Config('http://seerr', 'key', 'token', 'unused.db', activity_enabled=True, application_id='123', client_secret='private', guild_ids=frozenset([555]))
        self.bot.service.offload = MediaService.offload
        self.api = ActivityAPI(self.bot)
        self.api.discord_request = MagicMock(side_effect=[{'access_token': 'oauth', 'expires_in': 900}, {'id': '42'}, [{'id': '555'}]])
        server = WebhookServer(self.bot.service, asyncio.Event(), self.api)
        server.config = self.bot.config
        self.client = TestClient(TestServer(server.application()))
        await self.client.start_server()

    async def asyncTearDown(self):
        await self.client.close()

    async def test_oauth_identity_not_client_user_id(self):
        response = await self.client.post('/api/activity/auth', json={'code': 'one-use-code', 'guild_id': '555', 'discord_id': '999'})
        self.assertEqual(response.status, 200)
        body = await response.json()
        self.assertEqual(self.api.sessions[body['session_token']]['discord_id'], 42)
        self.assertNotIn('private', str(body))

    async def test_session_required_for_private_data(self):
        response = await self.client.post('/api/activity/action', json={'operation': 'requests', 'discord_id': '42'})
        self.assertEqual(response.status, 401)
        self.bot.service.run.assert_not_called()

    async def test_guild_membership_enforced(self):
        self.api.discord_request.side_effect = [{'access_token': 'oauth'}, {'id': '42'}, [{'id': '777'}]]
        response = await self.client.post('/api/activity/auth', json={'code': 'code', 'guild_id': '555'})
        self.assertEqual(response.status, 400)
        self.assertEqual(self.api.sessions, {})

    async def test_unknown_operations_rejected(self):
        self.api.sessions['session'] = {'discord_id': 42, 'expires': time.monotonic() + 60, 'oauth_token': 'oauth', 'guild_id': '555'}
        self.api.discord_request.side_effect = None
        self.api.discord_request.return_value = [{'id': '555'}]
        response = await self.client.post('/api/activity/action', json={'operation': 'delete_request'}, headers={'Authorization': 'Bearer session'})
        self.assertEqual(response.status, 400)
        self.bot.service.run.assert_not_called()

    async def authenticated_session(self):
        self.api.sessions['session'] = {'discord_id': 42, 'expires': time.monotonic() + 60, 'oauth_token': 'oauth', 'guild_id': '555'}
        self.api.discord_request.side_effect = None
        self.api.discord_request.return_value = [{'id': '555'}]
        return {'Authorization': 'Bearer session'}

    async def test_data_actions_use_verified_actor_and_existing_service(self):
        from unittest.mock import AsyncMock
        auth = await self.authenticated_session()
        self.bot.service.run = AsyncMock(return_value=[])
        response = await self.client.post('/api/activity/action', json={'operation': 'requests', 'discord_id': '999'}, headers=auth)
        self.assertEqual(response.status, 200)
        self.bot.service.run.assert_awaited_once_with(42, 'requests', False)

    async def test_deletion_requires_confirmation(self):
        auth = await self.authenticated_session()
        response = await self.client.post('/api/activity/action', json={'operation': 'delete', 'id': 1}, headers=auth)
        self.assertEqual(response.status, 400)
        self.bot.service.run.assert_not_called()

    async def test_undo_does_not_use_upstream_cache(self):
        from unittest.mock import AsyncMock
        auth = await self.authenticated_session()
        self.bot.service.cancel_deletion = AsyncMock(return_value='Undone')
        response = await self.client.post('/api/activity/action', json={'operation': 'undo', 'id': 1, 'discord_id': 999}, headers=auth)
        self.assertEqual(response.status, 200)
        self.bot.service.cancel_deletion.assert_awaited_once_with(42, 1)
        self.bot.service.run.assert_not_called()

    async def test_poster_proxy_rejects_non_image_names(self):
        response = await self.client.get('/api/activity/poster/secret.html')
        self.assertEqual(response.status, 400)

    async def test_admin_inbox_rejects_non_admin_even_with_claimed_id(self):
        auth = await self.authenticated_session()
        self.bot.admin.require_admin.side_effect = UserError('Operator access required.')
        response = await self.client.post('/api/activity/action', json={'operation': 'admin_inbox', 'discord_id': 99, 'is_admin': True}, headers=auth)
        self.assertEqual(response.status, 400)
        self.bot.admin.require_admin.assert_called_once_with(42)
        self.bot.admin.inbox.assert_not_called()

    async def test_admin_inbox_dispatches_filters_without_seerr_link(self):
        auth = await self.authenticated_session()
        self.bot.admin.inbox.return_value = {'messages': [], 'total': 0}
        response = await self.client.post('/api/activity/action', json={'operation': 'admin_inbox', 'kind': 'dm', 'user_id': '1234567890123456789', 'query': 'film', 'page': 2, 'order': 'oldest'}, headers=auth)
        self.assertEqual(response.status, 200)
        self.bot.admin.inbox.assert_called_once_with(42, kind='dm', guild_id=None, user_id='1234567890123456789', channel_id=None, query='film', page=2, order='oldest')
        self.bot.service.account.assert_not_called()

    async def test_admin_prepare_accepts_full_discord_snowflakes(self):
        from unittest.mock import AsyncMock
        auth = await self.authenticated_session()
        self.bot.admin.prepare = AsyncMock(return_value={'plan': 'preview'})
        response = await self.client.post('/api/activity/action', json={'operation': 'admin_prepare', 'message': 'Hi', 'guild_ids': ['1234567890123456789']}, headers=auth)
        self.assertEqual(response.status, 200)
        self.bot.admin.prepare.assert_awaited_once_with(42, 'Hi', [1234567890123456789], user_id=None, all_users=False, channel_id=None)

    async def test_admin_send_requires_explicit_confirmation(self):
        auth = await self.authenticated_session()
        response = await self.client.post('/api/activity/action', json={'operation': 'admin_send', 'plan': 'preview'}, headers=auth)
        self.assertEqual(response.status, 400)
        self.bot.admin.confirm.assert_not_called()

    async def test_item_inputs_validated_and_raw_payloads_omitted(self):
        with self.assertRaises(UserError):
            item_reference({'kind': 'movie', 'external_id': '../../admin', 'title': 'Unsafe'})
        result = public_result([{'kind': 'movie', 'title': 'Film', 'raw_json': 'private', 'raw': {'key': 'secret'}}])
        self.assertNotIn('raw', result[0])
        self.assertNotIn('raw_json', result[0])


if __name__ == '__main__':
    unittest.main()
