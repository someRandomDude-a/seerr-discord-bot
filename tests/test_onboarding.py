import json
import asyncio
import os
import stat
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock, MagicMock, patch

from aiohttp import CookieJar, web
from aiohttp.test_utils import TestClient, TestServer

from media_bot.config import Config
from media_bot.discovery import discover_arr
from media_bot.panel import ControlPanel
from media_bot.settings import load_settings, save_settings
from media_bot.security import UserError


class SavedSettingsTests(unittest.TestCase):
    def test_private_atomic_settings_and_environment_precedence(self):
        with tempfile.TemporaryDirectory() as temp, patch.dict(os.environ, {'DATA_DIR': temp}, clear=True):
            path = Path(temp) / 'settings.json'
            saved = {'SEERR_URL': 'http://seerr', 'SEERR_ADMIN_KEY': 'private', 'DISCORD_TOKEN': 'saved-token'}
            save_settings(saved)
            self.assertEqual(Config.from_env().discord_token, 'saved-token')
            with patch.dict(os.environ, {'DISCORD_TOKEN': 'env-token'}):
                self.assertEqual(Config.from_env().discord_token, 'env-token')
            self.assertEqual(load_settings(), saved)
            self.assertEqual(list(Path(temp).glob('.settings-*')), [])
            if os.name != 'nt':
                self.assertEqual(stat.S_IMODE(path.stat().st_mode), 0o600)

    def test_invalid_saved_settings_not_silently_overwritten(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / 'settings.json'; path.write_text('{"version":99}', encoding='utf-8')
            with self.assertRaises(ValueError):
                load_settings(path)
            self.assertEqual(path.read_text(), '{"version":99}')


class DiscoveryTests(unittest.TestCase):
    def test_multiple_servers_ssl_base_paths_ipv6_and_secret_repr(self):
        api = MagicMock()
        api._request.side_effect = [[
            {'id': 0, 'hostname': 'radarr', 'port': 7878, 'apiKey': 'secret-a', 'baseUrl': '/movies'},
            {'id': 1, 'hostname': '::1', 'port': 443, 'apiKey': 'secret-b', 'useSsl': True, 'is4k': True},
        ], [{'id': 4, 'hostname': 'sonarr', 'port': 8989, 'apiKey': 'secret-c'}]]
        result = discover_arr(api)
        self.assertEqual(set(result), {'radarr:0', 'radarr:1', 'sonarr:4'})
        self.assertEqual(result['radarr:0'].url, 'http://radarr:7878/movies')
        self.assertEqual(result['radarr:1'].url, 'https://[::1]:443')
        self.assertNotIn('secret-b', repr(result))

    def test_invalid_or_partial_configuration_blocks_discovery(self):
        for rows in ({'error': 'bad'}, [{'id': 1, 'hostname': 'radarr', 'port': 0, 'apiKey': 'key'}],
                     [{'id': 1, 'hostname': 'radarr', 'port': 7878, 'apiKey': ''}]):
            api = MagicMock(); api._request.return_value = rows
            with self.subTest(rows=rows), self.assertRaises(ValueError):
                discover_arr(api)


class ConnectionVerificationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.panel = ControlPanel(path=Path(self.temp.name) / 'settings.json')

    def test_discord_token_guilds_intents_and_human_exceptions(self):
        responses = [
            {'bot': True, 'username': 'Media'}, {'id': '123', 'flags': (1 << 15) | (1 << 19)},
            [{'id': '456', 'name': 'Cinema'}], {'bot': False},
        ]
        with patch('media_bot.panel.requests.get') as get:
            get.side_effect = [MagicMock(status_code=200, json=MagicMock(return_value=row)) for row in responses]
            result = self.panel.verify('discord', {'DISCORD_TOKEN': 'private-token', 'ALLOWED_GUILD_IDS': '456',
                'ENABLE_MEMBERS_INTENT': 'true', 'INBOX_MESSAGE_CONTENT': 'true', 'ADMIN_DISCORD_IDS': '789'})
        self.assertEqual(result['application_id'], '123')
        self.assertEqual(result['guilds'], [{'id': '456', 'name': 'Cinema'}])
        self.assertNotIn('private-token', json.dumps(result))
        for call in get.call_args_list:
            self.assertFalse(call.kwargs['allow_redirects'])
            self.assertEqual(call.kwargs['timeout'], 15)

    def test_discord_rejects_user_tokens_and_unjoined_guilds(self):
        for identity, selected in [({'bot': False}, ''), ({'bot': True, 'username': 'Media'}, '999')]:
            with self.subTest(identity=identity), patch('media_bot.panel.requests.get') as get:
                get.side_effect = [MagicMock(status_code=200, json=MagicMock(return_value=row))
                    for row in (identity, {'id': '123'}, [])]
                with self.assertRaises(UserError):
                    self.panel.verify('discord', {'DISCORD_TOKEN': 'token', 'ALLOWED_GUILD_IDS': selected})

    def test_seerr_discovers_every_instance_and_lists_admins_without_promoting_users(self):
        with patch('media_bot.panel.SeerrAPI') as api_type, patch('media_bot.panel.ArrClient') as client_type:
            api = api_type.return_value
            api.get_current_user.return_value = {'id': 1}
            api._request.side_effect = [[{'id': 0, 'hostname': 'radarr', 'port': 7878, 'apiKey': 'private-radarr'},
                {'id': 1, 'hostname': 'radarr4k', 'port': 7878, 'apiKey': 'private-4k'}], []]
            api.list_users.return_value = {'results': [{'id': 1, 'username': 'Owner'},
                {'id': 2, 'permissions': 2, 'displayName': 'Admin'}, {'id': 3, 'permissions': 32}]}
            result = self.panel.verify('seerr', {'SEERR_URL': 'http://seerr', 'SEERR_ADMIN_KEY': 'private-key'})
            self.assertEqual([s['name'] for s in result['services']], ['radarr:0', 'radarr:1'])
            self.assertEqual([u['id'] for u in result['admins']], [1, 2])
            self.assertEqual(client_type.call_count, 2)
            api.update_user.assert_not_called()
            api.close.assert_called_once()
            self.assertNotIn('private-', json.dumps(result))

    def test_seerr_rejects_non_admin_and_closes_its_session(self):
        with patch('media_bot.panel.SeerrAPI') as api_type:
            api = api_type.return_value; api.get_current_user.return_value = {'id': 2, 'permissions': 32}
            with self.assertRaises(UserError):
                self.panel.verify('seerr', {'SEERR_URL': 'http://seerr', 'SEERR_ADMIN_KEY': 'key'})
            api.close.assert_called_once(); api._request.assert_not_called()

    def test_optional_connection_choices_exclude_upstream_secrets_and_require_valid_selection(self):
        with patch('media_bot.panel.ArrClient') as client_type:
            client_type.return_value.request.side_effect = lambda method, path: {
                'system/status': {}, 'qualityprofile': [{'id': 1, 'name': 'Standard', 'secret': 'not-for-browser'}],
                'metadataprofile': [{'id': 2, 'name': 'All'}], 'rootfolder': [{'id': 3, 'path': '/music'}]}[path]
            values = {'LIDARR_URL': 'http://lidarr', 'LIDARR_API_KEY': 'key', 'LIDARR_QUALITY_PROFILE_ID': '1'}
            result = self.panel.verify('lidarr', values)
            self.assertEqual(result['choices']['rootfolder'], [{'id': 3, 'path': '/music'}])
            self.assertNotIn('not-for-browser', json.dumps(result))
            with self.assertRaises(UserError):
                self.panel.verify('lidarr', {**values, 'LIDARR_QUALITY_PROFILE_ID': '99'})
        self.assertEqual(self.panel.verify('readarr', {}), {'disabled': True})


class PanelTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.environment = patch.dict(os.environ, {'DATA_DIR': self.temp.name}, clear=True)
        self.environment.start()
        self.panel = ControlPanel(path=Path(self.temp.name) / 'settings.json')
        self.code = self.panel.code
        self.panel.verify = MagicMock(side_effect=lambda operation, values:
            {'application_id': '123', 'bot': 'Media', 'guilds': [], 'install_url': 'https://discord.com'} if operation == 'discord'
            else {'message': 'Verified'})
        self.client = TestClient(TestServer(self.panel.application()), cookie_jar=CookieJar(unsafe=True))
        await self.client.start_server()
        self.origin = str(self.client.make_url('/')).rstrip('/')

    async def asyncTearDown(self):
        await self.client.close()
        self.environment.stop()
        self.temp.cleanup()

    async def post(self, path, data, origin=None):
        return await self.client.post(path, json=data, headers={'Origin': self.origin if origin is None else origin})

    async def login(self):
        response = await self.post('/api/login', {'code': self.code})
        self.assertEqual(response.status, 200)
        cookie = next(value for name, value in response.cookies.items() if name.startswith('panel_session_'))
        self.assertTrue(cookie['httponly'])
        self.assertEqual(cookie['samesite'], 'Strict')

    async def verify_required(self):
        response = await self.post('/api/action', {'operation': 'discord', 'changes': {'DISCORD_TOKEN': 'private-discord-token'}})
        self.assertEqual(response.status, 200)
        response = await self.post('/api/action', {'operation': 'seerr', 'changes': {'SEERR_URL': 'http://seerr', 'SEERR_ADMIN_KEY': 'private-seerr-key'}})
        self.assertEqual(response.status, 200)

    async def test_private_routes_require_session_and_one_time_code(self):
        self.assertEqual((await self.client.get('/api/state')).status, 401)
        self.assertEqual((await self.post('/api/login', {'code': 'wrong'})).status, 401)
        await self.login()
        self.assertEqual((await self.post('/api/login', {'code': self.code})).status, 401)

    async def test_panels_on_different_localhost_ports_do_not_overwrite_each_others_cookies(self):
        await self.login()
        second_panel = ControlPanel(path=self.panel.path)
        second = TestClient(TestServer(second_panel.application()), cookie_jar=self.client.session.cookie_jar)
        await second.start_server()
        try:
            origin = str(second.make_url('/')).rstrip('/')
            response = await second.post('/api/login', json={'code': second_panel.code}, headers={'Origin': origin})
            self.assertEqual(response.status, 200)
            names = [cookie.key for cookie in self.client.session.cookie_jar if cookie.key.startswith('panel_session_')]
            self.assertEqual(len(set(names)), 2)
            self.assertEqual((await self.client.get('/api/state')).status, 200)
            self.assertEqual((await second.get('/api/state')).status, 200)
            # An unrelated application's legacy cookie cannot stomp either panel.
            self.client.session.cookie_jar.update_cookies({'panel_session': 'unrelated-app'}, response_url=self.client.make_url('/'))
            self.assertEqual((await self.client.get('/api/state')).status, 200)
        finally:
            await second.close()

    async def test_wrong_origin_cannot_revoke_a_valid_panel_session(self):
        await self.login()
        token = next(iter(self.panel.sessions))
        request = SimpleNamespace(host='127.0.0.1:1', cookies={})
        request.cookies[self.panel.cookie_name(request)] = token
        with self.assertRaises(web.HTTPForbidden):
            self.panel.session(request)
        self.assertIn(token, self.panel.sessions)
        self.assertEqual((await self.client.get('/api/state')).status, 200)

    async def test_eight_hour_expiry_prints_one_replacement_code_and_allows_reauthentication(self):
        with patch('media_bot.panel.time') as clock, patch('builtins.print') as output:
            clock.monotonic.return_value = 10.0
            await self.login()
            clock.monotonic.return_value = 10 + 8 * 3600 - .001
            self.assertEqual((await self.client.get('/api/state')).status, 200)
            clock.monotonic.return_value = 10 + 8 * 3600
            response = await self.client.get('/api/state')
            self.assertEqual(response.status, 401)
            replacement = self.panel.code
            self.assertTrue(replacement)
            self.assertNotEqual(replacement, self.code)
            self.assertNotIn(replacement, await response.text())
            self.assertEqual((await self.client.get('/api/state')).status, 401)
            output.assert_called_once()
            self.assertIn(replacement, output.call_args.args[0])
            self.assertEqual((await self.post('/api/login', {'code': self.code})).status, 401)
            self.assertEqual((await self.post('/api/login', {'code': replacement})).status, 200)
            self.assertEqual((await self.client.get('/api/state')).status, 200)
            self.assertEqual((await self.post('/api/login', {'code': replacement})).status, 401)

    async def test_missing_cookie_does_not_rotate_code_or_invalidate_an_active_session(self):
        await self.login()
        token = next(iter(self.panel.sessions))
        request = SimpleNamespace(host=self.panel.sessions[token]['host'], cookies={})
        with patch('builtins.print') as output, self.assertRaises(web.HTTPUnauthorized):
            self.panel.session(request)
        output.assert_not_called()
        self.assertEqual(self.panel.code, '')
        self.assertIn(token, self.panel.sessions)
        self.assertEqual((await self.client.get('/api/state')).status, 200)

    async def test_csrf_content_type_and_dns_rebinding_rejected(self):
        self.assertEqual((await self.post('/api/login', {'code': self.code}, origin='http://evil.example')).status, 403)
        response = await self.client.post('/api/login', data={'code': self.code}, headers={'Origin': self.origin})
        self.assertEqual(response.status, 415)
        response = await self.client.get('/', headers={'Host': 'evil.example'})
        self.assertEqual(response.status, 403)

    async def test_saved_credentials_are_not_returned_to_forms(self):
        await self.login(); await self.verify_required()
        response = await self.client.get('/api/state'); body = await response.json()
        self.assertIs(body['values']['DISCORD_TOKEN'], True)
        self.assertNotIn('private-discord-token', json.dumps(body))
        self.assertNotIn('private-seerr-key', json.dumps(body))
        self.assertEqual(response.headers['Cache-Control'], 'no-store')
        self.assertEqual(response.headers['X-Frame-Options'], 'DENY')

    async def test_save_requires_current_verified_settings_and_sets_reload_event(self):
        await self.login()
        response = await self.post('/api/action', {'operation': 'save'})
        self.assertEqual(response.status, 400)
        self.assertFalse(self.panel.path.exists())
        await self.verify_required()
        response = await self.post('/api/action', {'operation': 'save'})
        self.assertEqual(response.status, 200)
        self.assertTrue(self.panel.changed.is_set())
        self.assertEqual((await self.client.get('/api/state')).status, 200)
        self.assertEqual(load_settings(self.panel.path)['DISCORD_TOKEN'], 'private-discord-token')
        response = await self.post('/api/action', {'operation': 'save', 'changes': {'SEERR_URL': 'http://other-seerr'}})
        self.assertEqual(response.status, 400)
        self.assertEqual(load_settings(self.panel.path)['SEERR_URL'], 'http://seerr')

    async def test_verification_expires_and_changes_do_not_change_environment(self):
        await self.login(); await self.verify_required()
        session = next(iter(self.panel.sessions.values()))
        digest, verified_at = session['verified']['discord']
        session['verified']['discord'] = (digest, verified_at - 601)
        self.assertEqual((await self.post('/api/action', {'operation': 'save'})).status, 400)
        self.assertNotIn('DISCORD_TOKEN', os.environ)

    async def test_verification_expiry_boundary_on_a_freshly_started_clock(self):
        # Monotonic clocks have arbitrary origins; a new runner may be below 600.
        # Patch only the panel's module reference, not asyncio's own clock.
        with patch('media_bot.panel.time') as clock:
            clock.monotonic.return_value = 10.0
            await self.login(); await self.verify_required()
            clock.monotonic.return_value = 609.999
            state = await (await self.client.get('/api/state')).json()
            self.assertEqual(set(state['verified']), {'discord', 'seerr'})
            self.assertEqual((await self.post('/api/action', {'operation': 'save'})).status, 200)
            saved = self.panel.path.read_bytes()
            self.panel.changed.clear()
            clock.monotonic.return_value = 610.0
            state = await (await self.client.get('/api/state')).json()
            self.assertEqual(state['verified'], [])
            self.assertEqual((await self.post('/api/action', {'operation': 'save'})).status, 400)
            self.assertFalse(self.panel.changed.is_set())
            self.assertEqual(self.panel.path.read_bytes(), saved)

    async def test_locked_environment_and_unknown_fields_rejected(self):
        await self.login()
        for changes in ({'DATA_DIR': '/evil'}, {'UNKNOWN': 'bad'}):
            self.assertEqual((await self.post('/api/action', {'operation': 'discord', 'changes': changes})).status, 400)
        with patch.dict(os.environ, {'DISCORD_TOKEN': 'environment-secret'}):
            response = await self.post('/api/action', {'operation': 'discord', 'changes': {'DISCORD_TOKEN': 'replacement'}})
            self.assertEqual(response.status, 400)

    async def test_broadcast_requires_confirmation_even_for_local_operator(self):
        await self.login()
        bot = MagicMock(); bot.is_ready.return_value = True; bot.activity = None; bot.webhooks.last_tests = {}; self.panel.bot = bot
        response = await self.post('/api/admin', {'operation': 'send', 'plan': 'preview', 'actor': 42})
        self.assertEqual(response.status, 400)
        bot.admin.confirm.assert_not_called()

    async def test_webhook_credential_generated_and_revealed_only_to_operator(self):
        self.assertEqual((await self.post('/api/action', {'operation': 'generate_webhook'})).status, 401)
        await self.login()
        response = await self.post('/api/action', {'operation': 'generate_webhook'}); body = await response.json()
        secret = body['result']['secret']; self.assertGreaterEqual(len(secret), 32)
        response = await self.post('/api/action', {'operation': 'webhook_info'}); body = await response.json()
        self.assertEqual(body['result']['secret'], secret)

    async def test_public_webhook_test_requires_backend_echo_and_does_not_follow_redirects(self):
        await self.login()
        bot = MagicMock(); bot.is_ready.return_value = True
        bot.config = Config('http://seerr', 'key', 'token', 'unused', webhook_public_url='https://hub.example',
            webhook_secret='x' * 32)
        self.panel.bot = bot
        values = {'WEBHOOK_PUBLIC_URL': 'https://hub.example/', 'WEBHOOK_SECRET': 'x' * 32}
        with patch('media_bot.panel.requests.post') as post:
            post.return_value = MagicMock(status_code=200, json=MagicMock(return_value={'accepted': True}))
            with self.assertRaises(UserError):
                await self.panel.public_test(values)
            post.side_effect = lambda *args, **kwargs: MagicMock(status_code=200,
                json=MagicMock(return_value={'verification': kwargs['json']['verification']}))
            result = await self.panel.public_test(values)
            self.assertIn('webhook', result)
            self.assertFalse(post.call_args.kwargs['allow_redirects'])

    async def test_admin_identity_is_internal_not_browser_claimed(self):
        from media_bot.service import MediaService
        await self.login()
        bot = MagicMock(); bot.is_ready.return_value = True; bot.service.offload = MediaService.offload
        bot.admin.state.return_value = {'guilds': []}; self.panel.bot = bot
        response = await self.post('/api/admin', {'operation': 'state', 'actor': '999', 'is_admin': True})
        self.assertEqual(response.status, 200)
        bot.admin.state.assert_called_once_with('local-panel')

    async def test_random_port_and_assets_work_without_discord_or_seerr(self):
        panel = ControlPanel(path=self.panel.path)
        try:
            port = await panel.start()
            self.assertGreater(port, 0)
            self.assertNotEqual(port, 8080)
        finally:
            await panel.close()
        for path in ('/', '/panel.js', '/panel.css', '/glass.css'):
            self.assertEqual((await self.client.get(path)).status, 200)


class ApplicationLifecycleTests(unittest.IsolatedAsyncioTestCase):
    async def test_panel_survives_incomplete_config_and_bot_login_failure_and_can_restart(self):
        from bot import run_application
        with tempfile.TemporaryDirectory() as temp, patch.dict(os.environ, {}, clear=True):
            panel = ControlPanel(path=Path(temp) / 'settings.json')
            panel.start = AsyncMock(); panel.close = AsyncMock()
            configured, failed, restarted = asyncio.Event(), asyncio.Event(), asyncio.Event()
            config = Config('http://seerr', 'key', 'token', str(Path(temp) / 'cache.db'))
            def read_config():
                if not configured.is_set():
                    configured.set()
                    raise ValueError('Missing token')
                return config
            first, second = MagicMock(), MagicMock()
            first.start = AsyncMock(side_effect=RuntimeError('Rejected token'))
            async def first_close():
                failed.set()
            first.close = AsyncMock(side_effect=first_close)
            async def second_start(token):
                restarted.set()
                await asyncio.Event().wait()
            second.start = AsyncMock(side_effect=second_start); second.close = AsyncMock()
            with patch('media_bot.panel.ControlPanel', return_value=panel), patch('bot.Config.from_env', side_effect=read_config), \
                    patch('bot.SeerrBot', side_effect=[first, second]), patch('builtins.print'):
                task = asyncio.create_task(run_application())
                try:
                    await asyncio.wait_for(configured.wait(), 2)
                    self.assertIn('Configuration/storage', panel.runtime_error)
                    self.assertFalse(task.done())
                    panel.changed.set()
                    await asyncio.wait_for(failed.wait(), 2)
                    self.assertIn('RuntimeError', panel.runtime_error)
                    self.assertFalse(task.done())
                    panel.changed.set()
                    await asyncio.wait_for(restarted.wait(), 2)
                    self.assertIs(panel.bot, second)
                    self.assertEqual(panel.runtime_error, '')
                finally:
                    task.cancel()
                    await asyncio.gather(task, return_exceptions=True)
                panel.close.assert_awaited_once()
