import asyncio
import json
import os
import tempfile
import time
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock, MagicMock, patch

import requests
from aiohttp.test_utils import TestClient, TestServer

from bot import SeerrBot
from media_bot.activity import ActivityAPI
from media_bot.config import Config
from media_bot.jellyfin import JellyfinConfig, discover_jellyfin
from media_bot.security import RateLimiter, VerificationRequired, VerificationUnavailable
from media_bot.service import MediaService
from media_bot.ui import DetailView, ItemsView
from media_bot.webhooks import WebhookServer


USER_ID, SERVER_ID, ITEM_ID = 'a' * 32, 'b' * 32, 'c' * 32


class IdentityTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.config = Config('http://seerr', 'admin-key', 'discord-token', str(Path(self.temp.name) / 'cache.db'),
            devices={'Private device label': 'https://private-jellyfin.example'}, admin_ids=frozenset([42]))
        self.service = MediaService(self.config)
        self.credential = {'url': 'http://jellyfin:8096', 'device_id': 'discord-device',
            'token': 'PRIVATE-USER-TOKEN', 'user_id': USER_ID, 'server_id': SERVER_ID}
        self.settings = JellyfinConfig('http://jellyfin:8096', 'https://jellyfin.example', SERVER_ID)
        self.settings_patch = patch.object(MediaService, 'jellyfin_settings', return_value=self.settings)
        self.settings_mock = self.settings_patch.start()
        self.http_patch = patch('media_bot.jellyfin.requests.request')
        self.http = self.http_patch.start()
        self.response = MagicMock(status_code=200)
        self.response.json.return_value = {'Id': USER_ID, 'ServerId': SERVER_ID, 'Policy': {}}
        self.http.return_value = self.response
        bot = SimpleNamespace(config=self.config, service=self.service, limiter=RateLimiter(100, 60), admin=MagicMock())
        self.activity = ActivityAPI(bot)
        self.activity.sessions['session'] = {'discord_id': 42, 'guild_id': None, 'oauth_token': 'discord-oauth',
            'expires': time.monotonic() + 600}
        self.auth = {'Authorization': 'Bearer session'}
        self.web = WebhookServer(self.service, asyncio.Event(), self.activity)
        self.client = TestClient(TestServer(self.web.application()))
        await self.client.start_server()

    async def asyncTearDown(self):
        await self.client.close()
        self.service.api.close()
        self.http_patch.stop()
        self.settings_patch.stop()
        self.temp.cleanup()

    def link(self):
        self.service.store.link(42, 7, 'Private account name', self.credential)

    async def action(self, operation):
        return await self.client.post('/api/activity/action', json={'operation': operation}, headers=self.auth)

    async def test_unlinked_profile_and_public_bootstrap_reveal_no_server_details(self):
        config = await (await self.client.get('/api/activity/config')).json()
        self.assertEqual(set(config), {'application_id', 'scopes'})
        profile = await (await self.action('profile')).json()
        self.assertEqual(profile, {'account': None, 'devices': [], 'is_admin': False, 'verification_required': True})
        self.settings_mock.assert_not_called()
        self.http.assert_not_called()

    async def test_legacy_link_and_operator_exception_are_not_identity_proof(self):
        self.service.store.link(42, 7, 'Private account name')
        self.assertFalse(self.service.is_admin(42))
        for operation in ('profile', 'status', 'watches', 'deletions', 'admin_state', 'dashboard', 'library', 'requests', 'storage', 'seerr_link', 'sync_watchlist', 'import_watchlist'):
            response = await self.action(operation)
            body = await response.json()
            self.assertNotIn('Private account name', json.dumps(body))
            self.assertNotIn('Private device label', json.dumps(body))
            if operation != 'profile':
                self.assertTrue(body['verification_required'])
        self.http.assert_not_called()
        self.activity.bot.admin.state.assert_not_called()

    async def test_browse_bundle_uses_session_identity_and_never_serializes_service_credentials(self):
        self.link()
        self.config.admin_ids = frozenset()
        api = MagicMock()
        api.get_current_user.return_value = {'id': 7, 'jellyfinUserId': USER_ID, 'permissions': 32}
        api.get_notification_settings.return_value = {'discordIds': ['42']}
        api._request.return_value = {'results': [{'mediaType': 'movie', 'id': 10, 'title': 'Film', 'posterPath': '/a.jpg', 'raw': {'secret': 'admin-key'}}]}
        with patch('media_bot.service.SeerrAPI', return_value=api) as factory:
            response = await self.client.post('/api/activity/action', json={'operation': 'browse', 'page': 'search', 'kind': 'movie', 'discord_id': 999, 'is_admin': True}, headers=self.auth)
        self.assertEqual(response.status, 200)
        body = await response.json()
        self.assertEqual(body['viewer']['account']['discord_id'], '42')
        self.assertFalse(body['viewer']['is_admin'])
        self.assertEqual(body['result'][0]['poster_path'], '/a.jpg')
        self.assertNotIn('admin-key', json.dumps(body))
        self.assertNotIn('PRIVATE-USER-TOKEN', json.dumps(body))
        factory.assert_called_once_with('http://seerr', 'admin-key', 15, 7)

    async def test_unverified_browse_bundle_discloses_neither_metadata_nor_artwork(self):
        self.service.store.link(42, 7, 'Private account name')
        response = await self.client.post('/api/activity/action', json={'operation': 'browse', 'page': 'search', 'kind': 'movie'}, headers=self.auth)
        body = await response.json()
        self.assertEqual(response.status, 400)
        self.assertTrue(body['verification_required'])
        self.assertNotIn('viewer', body)
        self.assertNotIn('result', body)
        self.settings_mock.assert_not_called()

    async def test_live_detail_serialization_excludes_private_upstream_payloads(self):
        from media_bot.activity import public_result
        result = public_result({'kind': 'music', 'external_id': 'album-id', 'title': 'Album',
            'overview': 'Synopsis', 'raw': {'artist': {'path': '/private', 'apiKey': 'secret'}}, 'payload': 'private'})
        self.assertEqual(result['overview'], 'Synopsis')
        self.assertNotIn('raw', result)
        self.assertNotIn('payload', result)

    async def test_background_watchlist_sync_rejects_revoked_identity_without_touching_upstream(self):
        self.link()
        self.service.store.watch(42, 'movie', 10, 'Film')
        self.response.status_code = 401
        with patch('media_bot.service.SeerrAPI') as factory:
            await self.service.sync_watchlists()
        factory.assert_not_called()
        self.assertEqual(len(self.service.store.watches(42)), 1)
        with self.service.store.connect() as db:
            self.assertEqual(db.execute('SELECT COUNT(*) FROM watchlist_intents').fetchone()[0], 1)

    async def test_activity_watchlist_sync_uses_verified_session_user_not_supplied_user_id(self):
        from test_watchlists import WatchlistAPI
        self.link()
        self.service.store.watch(42, 'movie', 10, 'Film')
        remote = WatchlistAPI()
        api = MagicMock()
        api.get_current_user.return_value = {'id': 7, 'jellyfinUserId': USER_ID}
        api.get_notification_settings.return_value = {'discordIds': ['42']}
        api._request.side_effect = remote._request
        with patch('media_bot.service.SeerrAPI', return_value=api) as factory:
            response = await self.client.post('/api/activity/action', json={'operation': 'sync_watchlist', 'discord_id': 999, 'seerr_id': 1}, headers=self.auth)
        self.assertEqual(response.status, 200)
        self.assertIn('both ways', (await response.json())['result'])
        self.assertEqual(set(remote.items), {('movie', '10')})
        for call in factory.call_args_list:
            self.assertEqual(call.args, ('http://seerr', 'admin-key', 15, 7))
        self.assertNotIn('PRIVATE-USER-TOKEN', str(remote.calls))

    async def test_revoked_seerr_mapping_cannot_process_saved_watchlist_writes(self):
        self.link()
        self.service.store.watch(42, 'movie', 10, 'Film')
        api = MagicMock()
        api.get_current_user.return_value = {'id': 7, 'jellyfinUserId': USER_ID}
        api.get_notification_settings.return_value = {'discordIds': []}
        with patch('media_bot.service.SeerrAPI', return_value=api):
            await self.service.sync_watchlists()
        api._request.assert_not_called()

    async def test_token_encrypted_and_cold_restart_still_validates_jellyfin(self):
        self.link()
        with self.service.store.connect() as db:
            payload = db.execute('SELECT payload FROM jellyfin_credentials').fetchone()[0]
            db.execute('UPDATE accounts SET linked_at=0')  # No arbitrary identity TTL.
        self.assertNotIn('PRIVATE-USER-TOKEN', payload)
        self.assertNotIn('"token"', payload)
        key = Path(self.temp.name) / 'jellyfin.key'
        self.assertTrue(key.exists())
        if os.name != 'nt':
            self.assertEqual(key.stat().st_mode & 0o777, 0o600)
        restarted = MediaService(self.config)
        try:
            self.assertEqual(restarted.verify_identity(42)['seerr_id'], 7)
            self.assertEqual(restarted.store.credential(42), self.credential)
        finally:
            restarted.api.close()
        call = self.http.call_args
        self.assertEqual(call.args, ('GET', 'http://jellyfin:8096/Users/Me'))
        self.assertEqual(call.kwargs['headers']['X-Emby-Token'], 'PRIVATE-USER-TOKEN')
        self.assertNotIn('admin-key', str(call))
        self.assertFalse(call.kwargs['allow_redirects'])

    async def test_rejected_token_requires_reverification_but_keeps_permanent_binding(self):
        for status in (400, 401, 403, 404):
            with self.subTest(status=status):
                self.link()
                self.response.status_code = status
                with self.assertRaises(VerificationRequired):
                    self.service.status(42)
                self.assertIsNone(self.service.store.credential(42))
                self.assertEqual(self.service.store.bound_identity(42)['user_id'], USER_ID)
                self.assertEqual(self.service.profile(42)['devices'], [])

    async def test_outage_fails_closed_without_destroying_valid_credential(self):
        self.link()
        for failure in (requests.Timeout('private upstream URL'), None):
            self.http.side_effect = failure
            self.response.status_code = 503
            with self.subTest(failure=type(failure).__name__), patch.object(self.service, '_refresh') as refresh:
                with self.assertRaises(VerificationUnavailable):
                    await self.service.run(42, 'dashboard')
                refresh.assert_not_called()
                self.assertEqual(self.service.store.credential(42), self.credential)
        self.http.side_effect = None
        self.response.status_code = 200
        self.assertEqual(self.service.verify_identity(42)['seerr_id'], 7)

    async def test_disabled_deleted_or_different_identity_cannot_disclose_data(self):
        for user in ({'Id': 'd' * 32, 'ServerId': SERVER_ID}, {'Id': USER_ID, 'ServerId': 'e' * 32},
                     {'Id': USER_ID, 'ServerId': SERVER_ID, 'Policy': {'IsDisabled': True}}):
            self.link()
            self.response.json.return_value = user
            with self.assertRaises(VerificationRequired):
                await self.service.watches(42)
            self.assertIsNone(self.service.store.credential(42))

    async def test_credential_missing_or_unreadable_key_requires_new_approval(self):
        self.link()
        with patch.object(self.service.store.vault, 'decrypt', side_effect=ValueError('bad key')):
            self.assertTrue(self.service.profile(42)['verification_required'])
            with self.assertRaises(VerificationRequired):
                self.service.status(42)
        self.http.assert_not_called()

    async def test_server_configuration_change_does_not_forward_user_token_to_new_host(self):
        self.link()
        self.settings_mock.return_value = JellyfinConfig('http://other-host:8096', 'https://other.example', SERVER_ID)
        with self.assertRaises(VerificationRequired):
            self.service.verify_identity(42)
        self.http.assert_not_called()

    async def test_verified_profile_and_status_never_contain_user_token(self):
        self.link()
        profile = await (await self.action('profile')).json()
        self.assertEqual(profile['devices'], ['Browser', 'Private device label'])
        self.assertTrue(profile['is_admin'])
        self.assertEqual(profile['account']['name'], 'Private account name')
        self.assertNotIn('PRIVATE-USER-TOKEN', json.dumps(profile))
        self.assertIn('sync_interval', profile)
        self.assertEqual((await self.action('status')).status, 200)

    async def test_posters_require_identity_even_when_cached_or_token_in_query(self):
        self.activity.posters['a.jpg'] = (time.monotonic() + 300, b'private-image', 'image/jpeg')
        response = await self.client.get('/api/activity/poster/a.jpg?token=session')
        self.assertEqual(response.status, 401)
        response = await self.client.get('/api/activity/poster/a.jpg', headers=self.auth)
        self.assertEqual(response.status, 400)
        self.link()
        response = await self.client.get('/api/activity/poster/a.jpg', headers=self.auth)
        self.assertEqual(await response.read(), b'private-image')
        self.assertEqual(response.headers['Cache-Control'], 'no-store')
        self.response.status_code = 401
        response = await self.client.get('/api/activity/poster/a.jpg', headers=self.auth)
        self.assertTrue((await response.json())['verification_required'])
        self.assertNotEqual(await response.read(), b'private-image')

    async def test_profile_does_not_return_early_snapshot_if_later_check_finds_revocation(self):
        self.link()
        rejected = MagicMock(status_code=401)
        self.http.side_effect = [self.response, rejected]
        profile = self.service.profile(42)
        self.assertIsNone(profile['account'])
        self.assertEqual(profile['devices'], [])

    async def test_open_resolves_jellyfin_identity_and_destination_from_seerr(self):
        self.link()
        self.config.devices = {}
        user_api = MagicMock()
        user_api.get_movie_details.return_value = {'mediaInfo': {'status': 5}}  # No Jellyfin item ID in Seerr.
        self.response.json.return_value = {'Items': [{'Id': ITEM_ID, 'ProviderIds': {'Tmdb': '10'}}], 'TotalRecordCount': 1}
        url = self.service._op_open(self.service.account(42), user_api, {}, {'kind': 'movie', 'external_id': '10'})
        self.assertEqual(url, f'https://jellyfin.example/web/index.html#!/details?id={ITEM_ID}&serverId={SERVER_ID}')
        self.assertNotIn('TOKEN', url)
        self.assertEqual(self.http.call_args.kwargs['params']['AnyProviderIdEquals'], 'Tmdb.10')
        self.assertEqual(self.http.call_args.kwargs['params']['UserId'], USER_ID)

    async def test_open_rejects_inaccessible_ambiguous_or_mismatched_items(self):
        self.link()
        user_api = MagicMock()
        user_api.get_movie_details.return_value = {'mediaInfo': {'status': 5}}
        from media_bot.security import UserError
        for data in ({'Items': []}, {'Items': [{'Id': ITEM_ID, 'ProviderIds': {'Tmdb': '999'}}]},
                     {'Items': [{'Id': ITEM_ID, 'ProviderIds': {'Tmdb': '10'}},
                                {'Id': 'd' * 32, 'ProviderIds': {'Tmdb': '10'}}], 'TotalRecordCount': 2}):
            self.response.json.return_value = data
            with self.assertRaises(UserError):
                self.service._op_open(self.service.account(42), user_api, {}, {'kind': 'movie', 'external_id': '10'})

    async def test_open_preserves_selected_4k_edition_and_resolves_sonarr_tvdb_items(self):
        self.link()
        user_api = MagicMock()
        user_api.get_movie_details.return_value = {'mediaInfo': {'status': 5, 'status4k': 5,
            'jellyfinMediaId': 'd' * 32, 'jellyfinMediaId4k': ITEM_ID}}
        self.response.json.return_value = {'Items': [
            {'Id': 'd' * 32, 'ProviderIds': {'Tmdb': '10'}}, {'Id': ITEM_ID, 'ProviderIds': {'Tmdb': '10'}}], 'TotalRecordCount': 2}
        url = self.service._op_open(self.service.account(42), user_api, {}, {'kind': 'movie', 'external_id': '10', 'is4k': True})
        self.assertIn('id=' + ITEM_ID, url)
        self.response.json.return_value = {'Items': [{'Id': ITEM_ID, 'ProviderIds': {'Tvdb': '20'}}], 'TotalRecordCount': 1}
        url = self.service._op_open(self.service.account(42), user_api, {}, {'kind': 'tv', 'external_id': '20', 'source': 'sonarr:2'})
        self.assertIn('id=' + ITEM_ID, url)
        self.assertEqual(self.http.call_args.kwargs['params']['AnyProviderIdEquals'], 'Tvdb.20')
        user_api.get_tv_details.assert_not_called()

    async def test_item_access_denial_does_not_revoke_token_but_authentication_denial_does(self):
        self.link()
        from media_bot.security import UserError
        user_api = MagicMock()
        user_api.get_movie_details.return_value = {'mediaInfo': {}}
        self.response.status_code = 403
        with self.assertRaises(UserError):
            self.service._op_open(self.service.account(42), user_api, {}, {'kind': 'movie', 'external_id': '10'})
        self.assertIsNotNone(self.service.store.credential(42))
        self.response.status_code = 401
        with self.assertRaises(VerificationRequired):
            self.service._op_open(self.service.account(42), user_api, {}, {'kind': 'movie', 'external_id': '10'})
        self.assertIsNone(self.service.store.credential(42))

    async def test_relink_cannot_switch_identity_after_old_token_revoked(self):
        self.link()
        self.service.store.revoke_credential(42, self.credential['token'])
        user_api = MagicMock()
        user_api.get_current_user.return_value = {'id': 7, 'jellyfinUserId': 'd' * 32}
        with self.assertRaises(VerificationRequired):
            self.service.link_authenticated(42, user_api, dict(self.credential, user_id='d' * 32))
        user_api.update_notification_settings.assert_not_called()

    async def test_stale_revocation_cannot_delete_newly_approved_token(self):
        self.link()
        renewed = dict(self.credential, token='new-user-token')
        self.service.store.link(42, 7, 'Viewer', renewed)
        self.service.store.revoke_credential(42, self.credential['token'])
        self.assertEqual(self.service.store.credential(42), renewed)

    async def test_native_status_and_notifications_cannot_disclose_legacy_account(self):
        bot = SeerrBot(self.config)
        interaction = MagicMock()
        interaction.user.id = 42
        interaction.response.defer = AsyncMock()
        interaction.followup.send = AsyncMock()
        bot.service.store.link(42, 7, 'Private account name')
        try:
            for name in ('status', 'notifications'):
                with self.assertRaises(VerificationRequired):
                    await bot.tree.get_command(name).callback(interaction)
            parent = ItemsView(bot, 42, 'requests')
            detail = DetailView(bot, 42, {'id': 1, 'kind': 'movie', 'external_id': '10', 'title': 'Private film'}, parent)
            for operation in (detail.request, detail.delete):
                with self.assertRaises(VerificationRequired):
                    await operation(interaction)
            interaction.followup.send.assert_not_awaited()
            await bot.tree.get_command('notifications').callback(interaction, enabled=False)
            interaction.followup.send.assert_awaited_once_with('All personal updates muted.', ephemeral=True)
        finally:
            bot.service.api.close()

    async def test_notification_delivery_rechecks_token_and_preserves_opt_in_during_outage(self):
        self.link()
        bot = SeerrBot(self.config)
        target = MagicMock()
        target.send = AsyncMock()
        bot.get_user = MagicMock(return_value=target)
        user_api = MagicMock()
        user_api.get_current_user.return_value = {'id': 7, 'jellyfinUserId': USER_ID}
        user_api.get_notification_settings.return_value = {'discordIds': ['42']}
        with bot.service.store.connect() as db:
            db.execute("INSERT INTO outbox(discord_id,event_key,title,body) VALUES ('42','test','Private film','Private status')")
        bot.service.store.preference(42, opted_in=True)
        try:
            with patch('media_bot.service.SeerrAPI', return_value=user_api):
                self.response.status_code = 503
                await bot.deliver_notifications()
                target.send.assert_not_awaited()
                self.assertTrue(bot.service.account(42)['opted_in'])
                self.assertIsNotNone(bot.service.store.credential(42))
                with bot.service.store.connect() as db:
                    db.execute('UPDATE outbox SET next_attempt=0')
                self.response.status_code = 401
                await bot.deliver_notifications()
                target.send.assert_not_awaited()
                self.assertIsNone(bot.service.store.credential(42))
        finally:
            bot.service.api.close()


class JellyfinDiscoveryTests(unittest.TestCase):
    def test_seerr_config_discovery_preserves_base_path_and_external_destination(self):
        api = MagicMock()
        api._request.return_value = {'ip': 'jellyfin', 'port': 8096, 'urlBase': '/jellyfin/', 'useSsl': False,
            'externalHostname': 'https://media.example/jellyfin/web/index.html', 'serverId': SERVER_ID, 'apiKey': 'not-a-user-token'}
        result = discover_jellyfin(api)
        self.assertEqual(result.url, 'http://jellyfin:8096/jellyfin')
        self.assertEqual(result.external_url, 'https://media.example/jellyfin')
        self.assertNotIn('not-a-user-token', repr(result))
        api._request.assert_called_once_with('GET', '/settings/jellyfin')

    def test_internal_address_fallback_and_ipv6(self):
        api = MagicMock()
        api._request.return_value = {'ip': '::1', 'port': 8096, 'serverId': SERVER_ID}
        result = discover_jellyfin(api)
        self.assertEqual(result.url, 'http://[::1]:8096')
        self.assertEqual(result.external_url, result.url)
