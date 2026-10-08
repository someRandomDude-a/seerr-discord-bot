import asyncio
import json
import sqlite3
import tempfile
import time
import unittest
from pathlib import Path
from unittest.mock import MagicMock, patch

from media_bot.config import Config, ArrConfig
from media_bot.security import RateLimiter, UserError
from media_bot.service import MediaService
from media_bot.store import Store
from seerr.sync import SyncManager
from seerr.client import SeerrAPI
from seerr.exceptions import SeerrAPIError


def request(status=2, media_status=5):
    return {'id': 1, 'type': 'movie', 'status': status, 'is4k': False,
            'media': {'id': 3, 'tmdbId': 10, 'status': media_status, 'jellyfinMediaId': 'abc'},
            'requestedBy': {'id': 7, 'displayName': 'Viewer'},
            'createdAt': '2026-01-01', 'updatedAt': '2026-01-01'}


class SecurityTests(unittest.TestCase):
    def test_rate_limit_expires(self):
        clock = [0]
        limiter = RateLimiter(2, 60, lambda: clock[0])
        limiter.check('user')
        limiter.check('user')
        with self.assertRaises(UserError):
            limiter.check('user')
        clock[0] = 60
        limiter.check('user')


class ClientTests(unittest.TestCase):
    def test_impersonation_does_not_use_admin_identity(self):
        client = SeerrAPI('https://seerr.example/base', 'secret', user_id=7)
        self.assertEqual(client.session.headers['X-API-User'], '7')
        self.assertEqual(client.session.headers['Origin'], 'https://seerr.example')
        client.close()

    def test_csrf_bootstrap_and_header(self):
        client = SeerrAPI('https://seerr.example')
        response = MagicMock()
        response.status_code = 200
        response.text = '{}'
        response.json.return_value = {}
        def send(**kwargs):
            if kwargs['method'] == 'GET':
                client.session.cookies.set('XSRF-TOKEN', 'csrf%2Btoken')
            return response
        with patch.object(client.session, 'request', side_effect=send) as mocked:
            client.login_with_jellyfin('viewer', 'private')
            self.assertEqual(mocked.call_count, 2)
            self.assertEqual(mocked.call_args_list[0].kwargs['method'], 'GET')
            self.assertEqual(mocked.call_args_list[1].kwargs['headers'], {'X-XSRF-Token': 'csrf+token'})
            self.assertNotIn('X-Api-Key', client.session.headers)
            self.assertFalse(mocked.call_args.kwargs['allow_redirects'])
        client.close()

    def test_file_deletion_endpoint_only(self):
        client = SeerrAPI('https://seerr.example', 'secret')
        with patch.object(client, '_request') as mocked:
            client.delete_media_files(3, True)
            mocked.assert_called_once_with('DELETE', '/media/3/file', params={'is4k': 'true'})
        client.close()

    def test_no_redirects_with_credentials(self):
        client = SeerrAPI('https://seerr.example', 'secret')
        response = MagicMock(status_code=302)
        with patch.object(client.session, 'request', return_value=response):
            with self.assertRaises(SeerrAPIError):
                client.get_current_user()
        client.close()


class SnapshotTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.path = str(Path(self.tmp.name) / 'cache.db')
        self.api = MagicMock()
        self.api.list_requests.return_value = {'results': [request()]}
        self.api.get_movie_details.return_value = {'id': 10, 'title': 'A film', 'posterPath': '/poster.jpg'}
        self.sync = SyncManager(self.api, self.path)
        self.store = Store(self.path)

    def tearDown(self):
        self.tmp.cleanup()

    def rows(self):
        with self.store.connect() as db:
            return [dict(row) for row in db.execute('SELECT * FROM requests')]

    def test_partial_failure_preserves_snapshot(self):
        self.sync.sync()
        self.api.list_requests.side_effect = [{'results': [dict(request(), id=i) for i in range(50)]}, RuntimeError('offline')]
        with self.assertRaises(RuntimeError):
            self.sync.sync()
        self.assertEqual([r['id'] for r in self.rows()], [1])

    def test_empty_snapshot_clears_cache_and_updates_success(self):
        self.sync.sync()
        self.api.list_requests.return_value = {'results': []}
        self.sync.sync()
        self.assertEqual(self.rows(), [])
        with self.store.connect() as db:
            self.assertTrue(db.execute("SELECT value FROM sync_meta WHERE key='last_sync'").fetchone()[0])

    def test_media_changes_without_request_timestamp_changes(self):
        self.sync.sync()
        self.api.list_requests.return_value = {'results': [request(media_status=6)]}
        self.sync.sync()
        self.assertEqual(self.rows()[0]['media_status'], 6)
        self.assertEqual(self.rows()[0]['title'], 'A film')

    def test_unchanged_snapshot_still_advances_verification(self):
        self.sync.sync()
        with self.store.connect() as db:
            db.execute("UPDATE sync_meta SET value='old' WHERE key='last_sync'")
        self.sync.sync()
        with self.store.connect() as db:
            self.assertNotEqual(db.execute("SELECT value FROM sync_meta WHERE key='last_sync'").fetchone()[0], 'old')

    def test_incomplete_count_does_not_publish_partial_snapshot(self):
        self.sync.sync()
        self.api.list_requests.return_value = {'results': [], 'pageInfo': {'results': 10}}
        with self.assertRaises(RuntimeError):
            self.sync.sync()
        self.assertEqual(len(self.rows()), 1)

    def test_readonly_migration_explains_storage_permissions_and_preserves_data(self):
        path = Path(self.tmp.name) / 'legacy.db'
        with sqlite3.connect(path) as db:
            db.execute('PRAGMA journal_mode=WAL')
            db.executescript('''CREATE TABLE requests(id INTEGER PRIMARY KEY);
                CREATE TABLE media_details(media_id INTEGER PRIMARY KEY);
                CREATE TABLE sync_meta(key TEXT PRIMARY KEY,value TEXT);
                INSERT INTO requests VALUES (123);''')
        db.close()
        readonly = sqlite3.connect(path.as_uri() + '?mode=ro', uri=True)
        with patch('seerr.sync.sqlite3.connect', return_value=readonly):
            with self.assertRaisesRegex(PermissionError, 'UID 10001') as caught:
                SyncManager(self.api, str(path))
        self.assertIn(str(path), str(caught.exception))
        self.assertIn('-wal/-shm', str(caught.exception))
        with sqlite3.connect(path) as db:
            self.assertEqual(db.execute('SELECT id FROM requests').fetchone()[0], 123)
            self.assertEqual([r[1] for r in db.execute('PRAGMA table_info(requests)')], ['id'])
        db.close()

    def test_readonly_error_supports_python_without_named_sqlite_codes(self):
        connection = MagicMock()
        connection.execute.side_effect = sqlite3.OperationalError('attempt to write a readonly database')
        with patch('seerr.sync.sqlite3.connect', return_value=connection), \
                patch.dict(sqlite3.__dict__):
            sqlite3.__dict__.pop('SQLITE_READONLY', None)
            with self.assertRaisesRegex(PermissionError, 'UID 10001'):
                SyncManager(self.api, self.path)
        connection.close.assert_called_once()


class ServiceTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.config = Config('http://seerr', 'secret', 'token', str(Path(self.tmp.name) / 'cache.db'), 'https://bot.example',
                             devices={'Home': 'https://jellyfin.example'})
        self.service = MediaService(self.config)
        self.config.seerr_discovery = False  # Core fixtures supply snapshots directly; discovery has dedicated tests.
        self.service.api.close()
        self.service.api = MagicMock()
        self.service.sync.api = self.service.api
        self.service.api.list_requests.return_value = {'results': [request()]}
        self.service.api.get_movie_details.return_value = {'id': 10, 'title': 'A film'}
        self.service.api._request.return_value = {'results': []}
        self.service.store.link(42, 7, 'Viewer')
        self.account = self.service.account(42)
        self.user = {'id': 7, 'permissions': 32}
        self.user_api = MagicMock()
        self.user_api.get_current_user.return_value = self.user
        self.user_api.get_notification_settings.return_value = {'discordIds': ['42']}
        self.user_api.get_request.return_value = request()
        self.client_patch = patch('media_bot.service.SeerrAPI', return_value=self.user_api)
        self.client_patch.start()

    async def asyncTearDown(self):
        self.client_patch.stop()
        self.tmp.cleanup()

    def action(self, action_id):
        with self.service.store.connect() as db:
            return dict(db.execute('SELECT * FROM actions WHERE id=?', (action_id,)).fetchone())

    async def schedule(self):
        await self.service.run(42, 'delete', 1)
        return self.service.store.actions('delete')[0]['id']

    async def make_due(self, action_id):
        with self.service.store.connect() as db:
            db.execute('UPDATE actions SET execute_after=? WHERE id=?', (time.time() - 1, action_id))

    async def test_freshness_failure_blocks_read_and_mutation(self):
        self.service.api.list_requests.side_effect = RuntimeError('offline')
        with self.assertRaises(UserError):
            await self.service.run(42, 'requests', False)
        self.user_api.get_current_user.assert_not_called()
        self.user_api.create_request.assert_not_called()

    async def test_disabled_integrations_do_not_leave_stale_library_or_storage(self):
        with self.service.store.connect() as db:
            db.execute('INSERT INTO snapshots VALUES (?,?)', ('lidarr', '{"items":[],"disks":[{"path":"old"}]}'))
        await self.service.refresh()
        with self.service.store.connect() as db:
            self.assertIsNone(db.execute("SELECT * FROM snapshots WHERE source='lidarr'").fetchone())

    async def test_reimporting_full_watchlist_is_idempotent(self):
        for i in range(200):
            self.service.store.watch(42, 'movie', str(i), 'Film')
        self.service.store.watch(42, 'movie', '0', 'Updated title')
        self.assertEqual(len(self.service.store.watches(42)), 200)
        with self.assertRaises(UserError):
            self.service.store.watch(42, 'movie', '201', 'Extra film')

    async def test_link_revocation_blocks_access(self):
        self.user_api.get_notification_settings.return_value = {'discordIds': []}
        with self.assertRaises(UserError):
            await self.service.run(42, 'requests', False)

    async def test_seerr_admin_inference_requires_verified_link_and_live_admin_role(self):
        self.assertFalse(self.service.is_admin(999))
        self.assertFalse(self.service.is_admin(42))
        self.user_api.get_current_user.return_value = {'id': 7, 'permissions': 2}
        self.assertTrue(self.service.is_admin(42))
        self.user_api.get_current_user.return_value = {'id': 7, 'permissions': 32}
        self.assertFalse(self.service.is_admin(42))
        self.user_api.get_current_user.return_value = {'id': 7, 'permissions': 2}
        self.user_api.get_notification_settings.return_value = {'discordIds': []}
        self.assertFalse(self.service.is_admin(42))

    async def test_bot_only_exception_does_not_need_seerr_or_promote_permissions(self):
        self.config.admin_ids = frozenset([999])
        self.assertTrue(self.service.is_admin(999))
        self.user_api.get_current_user.assert_not_called()
        self.user_api.update_user.assert_not_called()

    async def test_profile_does_not_grant_admin_on_seerr_outage(self):
        self.user_api.get_current_user.side_effect = RuntimeError('offline')
        self.assertFalse(await self.service.verify_admin(42))

    async def test_discovery_refresh_failure_blocks_actions_instead_of_using_old_connections(self):
        self.config.seerr_discovery = True
        self.service.api._request.return_value = {'bad': 'not a settings list'}
        with self.assertRaises(UserError):
            await self.service.run(42, 'requests', False)
        self.user_api.get_current_user.assert_not_called()

    async def test_all_requests_require_seerr_permission(self):
        with self.assertRaises(UserError):
            await self.service.run(42, 'requests', True)
        self.user_api.get_current_user.return_value = {'id': 7, 'permissions': 16}
        self.assertEqual(len(await self.service.run(42, 'requests', True)), 1)

    async def test_deletion_is_delayed_24_hours(self):
        started = time.time()
        action_id = await self.schedule()
        self.assertGreaterEqual(self.action(action_id)['execute_after'], started + 86400)
        await self.service.process_due_deletions()
        self.service.api.delete_media_files.assert_not_called()
        self.assertEqual(self.action(action_id)['status'], 'pending')

    async def test_undo_even_while_services_are_offline(self):
        action_id = await self.schedule()
        self.service.api.list_requests.side_effect = RuntimeError('offline')
        await self.service.cancel_deletion(42, action_id)
        self.assertEqual(self.action(action_id)['status'], 'cancelled')
        self.service.api.delete_media_files.assert_not_called()

    async def test_undo_requires_owner(self):
        action_id = await self.schedule()
        self.service.store.link(99, 8, 'Other')
        with self.assertRaises(UserError):
            await self.service.cancel_deletion(99, action_id)
        self.assertEqual(self.action(action_id)['status'], 'pending')

    async def test_scheduled_deletion_survives_service_restart(self):
        action_id = await self.schedule()
        new_service = MediaService(self.config)
        try:
            self.assertEqual((await new_service.deletions(42))[0]['id'], action_id)
            await new_service.cancel_deletion(42, action_id)
            self.assertEqual(self.action(action_id)['status'], 'cancelled')
        finally:
            new_service.api.close()

    async def test_cancel_wins_while_network_lock_is_held(self):
        action_id = await self.schedule()
        async with self.service.lock:
            await asyncio.wait_for(self.service.cancel_deletion(42, action_id), timeout=1)
        self.assertEqual(self.action(action_id)['status'], 'cancelled')

    async def test_mute_works_offline(self):
        self.service.store.preference(42, opted_in=True)
        self.service.api.list_requests.side_effect = RuntimeError('offline')
        self.service.mute_notifications(42)
        self.assertFalse(self.service.account(42)['opted_in'])

    async def test_unfollow_works_even_if_watched_metadata_fails(self):
        self.service.store.watch(42, 'movie', 10, 'A film')
        self.service.api.list_requests.side_effect = RuntimeError('offline')
        items = await self.service.watches(42)
        await self.service.unwatch(42, items[0])
        self.assertEqual(await self.service.watches(42), [])

    async def test_due_deletion_only_deletes_files_preserves_requests(self):
        action_id = await self.schedule()
        await self.make_due(action_id)
        await self.service.process_due_deletions()
        self.service.api.delete_media_files.assert_called_once_with(3, False)
        self.service.api.delete_request.assert_not_called()
        self.assertEqual(self.action(action_id)['status'], 'completed')
        with self.service.store.connect() as db:
            self.assertEqual(db.execute('SELECT COUNT(*) FROM requests').fetchone()[0], 1)

    async def test_due_deletion_refresh_failure_delays_execution(self):
        action_id = await self.schedule()
        await self.make_due(action_id)
        self.service.api.list_requests.side_effect = RuntimeError('offline')
        await self.service.process_due_deletions()
        self.service.api.delete_media_files.assert_not_called()
        self.assertEqual(self.action(action_id)['status'], 'pending')

    async def test_transient_ownership_lookup_failure_remains_undoable(self):
        action_id = await self.schedule()
        await self.make_due(action_id)
        self.user_api.get_current_user.side_effect = ConnectionError()
        await self.service.process_due_deletions()
        self.assertEqual(self.action(action_id)['status'], 'pending')
        self.service.api.delete_media_files.assert_not_called()

    async def test_revoked_link_cancels_execution(self):
        action_id = await self.schedule()
        await self.make_due(action_id)
        self.user_api.get_notification_settings.return_value = {'discordIds': []}
        await self.service.process_due_deletions()
        self.assertEqual(self.action(action_id)['status'], 'cancelled')
        self.service.api.delete_media_files.assert_not_called()

    async def test_changed_owner_cancels_deletion(self):
        action_id = await self.schedule()
        await self.make_due(action_id)
        self.user_api.get_request.return_value = dict(request(), requestedBy={'id': 999})
        await self.service.process_due_deletions()
        self.service.api.delete_media_files.assert_not_called()
        self.assertEqual(self.action(action_id)['status'], 'cancelled')

    async def test_ambiguous_deletion_not_retried(self):
        action_id = await self.schedule()
        await self.make_due(action_id)
        self.service.api.delete_media_files.side_effect = TimeoutError()
        await self.service.process_due_deletions()
        await self.service.process_due_deletions()
        self.assertEqual(self.action(action_id)['status'], 'uncertain')
        self.service.api.delete_media_files.assert_called_once()

    async def test_schedule_rejects_duplicate_or_non_owner(self):
        await self.schedule()
        with self.assertRaises(UserError):
            await self.schedule()
        self.user_api.get_request.return_value = dict(request(), requestedBy={'id': 8})
        with self.assertRaises(UserError):
            await self.service.run(42, 'delete', 1)

    async def test_jellyfin_link_encodes_item_and_no_tokens(self):
        url = self.service.jellyfin_link(self.account, {'jellyfin_id': 'item?token=x'})
        self.assertEqual(url, 'https://jellyfin.example/web/index.html#!/details?id=item%3Ftoken%3Dx')

    async def test_notifications_opt_in_and_deduplicate(self):
        await self.service.refresh()  # Establish baseline without a notification flood.
        self.service.store.preference(42, opted_in=True)
        self.service.api.list_requests.return_value = {'results': [request(media_status=6)]}
        await self.service.refresh()
        await self.service.refresh()
        with self.service.store.connect() as db:
            rows = list(db.execute('SELECT * FROM outbox'))
        self.assertEqual(len(rows), 1)
        self.service.store.preference(42, opted_in=False)
        with self.service.store.connect() as db:
            self.assertEqual(db.execute('SELECT sent FROM outbox').fetchone()[0], 1)

    async def test_registration_preserves_other_settings(self):
        self.user_api.get_notification_settings.side_effect = [
            {'discordIds': [], 'pgpKey': 'existing', 'notificationTypes': {'email': 8}}, {'discordIds': ['42']}]
        self.service.link_authenticated(42, self.user_api)
        self.user_api.update_notification_settings.assert_called_once_with(7, {'discordIds': ['42'], 'pgpKey': 'existing', 'notificationTypes': {'email': 8}})

    async def test_registration_does_not_match_display_name_to_username(self):
        self.user_api.get_current_user.return_value = {'id': 7, 'displayName': 'Different Friendly Name'}
        self.user_api.get_notification_settings.return_value = {'discordIds': ['42']}
        self.assertEqual(self.service.link_authenticated(42, self.user_api), 'Different Friendly Name')

    async def test_registration_rejects_discord_conflict(self):
        self.user_api.get_notification_settings.return_value = {'discordIds': ['999']}
        with self.assertRaises(UserError):
            self.service.link_authenticated(42, self.user_api)
        self.user_api.update_notification_settings.assert_not_called()

    async def test_enabled_arr_failure_blocks_snapshot(self):
        self.service.arr['lidarr'] = MagicMock()
        self.service.arr['lidarr'].snapshot.side_effect = RuntimeError('offline')
        with self.assertRaises(UserError):
            await self.service.run(42, 'requests', False)
        self.assertTrue(self.service.store.meta()['error'])

    async def test_music_requests_submit_directly(self):
        arr = MagicMock()
        self.service.arr['lidarr'] = arr
        arr.snapshot.return_value = {'items': [], 'disks': []}
        arr.search.return_value = [{'foreignAlbumId': 'album', 'title': 'An album', 'artist': {'artistName': 'Artist'}}]
        message = await self.service.run(42, 'request', {'kind': 'music', 'external_id': 'album', 'title': 'An album'})
        self.assertIn('submitted', message)
        arr.add.assert_called_once()
        self.assertEqual(self.service.store.actions('add', pending=False)[0]['status'], 'completed')

    async def test_music_requests_require_permission(self):
        self.user_api.get_current_user.return_value = {'id': 7, 'permissions': 0}
        with self.assertRaises(UserError):
            await self.service.run(42, 'request', {'kind': 'music', 'external_id': 'album', 'title': 'An album'})

    async def test_watched_item_without_request_updates_notifications(self):
        self.service.api.list_requests.return_value = {'results': []}
        self.service.store.preference(42, opted_in=True)
        self.service.store.watch(42, 'movie', 22, 'Watch only')
        self.service.api.get_movie_details.return_value = {'mediaInfo': {'status': 2}}
        await self.service.refresh()
        self.service.api.get_movie_details.return_value = {'mediaInfo': {'status': 5}}
        await self.service.refresh()
        with self.service.store.connect() as db:
            rows = list(db.execute('SELECT * FROM outbox'))
        self.assertEqual(len(rows), 1)
        self.assertEqual(rows[0]['title'], 'Watch only')
        self.assertIn('Available', rows[0]['body'])


if __name__ == '__main__':
    unittest.main()
