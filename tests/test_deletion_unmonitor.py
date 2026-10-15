import copy
import json
import unittest
from unittest.mock import patch

import test_core as fixtures
from media_bot.config import ArrConfig
from media_bot.arr import ArrClient
from media_bot.discovery import deletion_service
from media_bot.security import UserError
from media_bot.service import MediaService


class DeletionUnmonitorTests(unittest.IsolatedAsyncioTestCase):
    action = fixtures.ServiceTests.action
    schedule = fixtures.ServiceTests.schedule
    make_due = fixtures.ServiceTests.make_due

    async def asyncSetUp(self):
        await fixtures.ServiceTests.asyncSetUp(self)
        self.kind = 'movie'
        self.events = []
        self.items = [{'id': 101, 'tmdbId': 10, 'title': 'Film', 'monitored': True, 'hasFile': True}]
        self.episodes = []
        self.remove_entry = False
        self.fail_monitoring = False
        self.fail_settings = False
        self.settings = [self.server(0, 'radarr', False, True), self.server(1, 'radarr4k', True, True)]
        def seerr(method, path, **kwargs):
            if path.startswith('/settings/'):
                if self.fail_settings:
                    raise ConnectionError('private address and key')
                return copy.deepcopy(self.settings)
            return {'results': []}
        self.service.api._request.side_effect = seerr
        def delete(media_id, is4k):
            self.events.append(('delete', media_id, is4k))
            self.assertFalse(any(event[0] == 'PUT' for event in self.events))
            if self.remove_entry:
                self.items = []
            else:
                for item in self.items:
                    item['hasFile'] = False
                    item['statistics'] = {'episodeFileCount': 0, 'sizeOnDisk': 0}
            req = copy.deepcopy(self.user_api.get_request.return_value)
            req['media']['status4k' if is4k else 'status'] = 6
            self.user_api.get_request.return_value = req
        self.service.api.delete_media_files.side_effect = delete
        self.arr_patch = patch('media_bot.arr.ArrClient.request', autospec=True, side_effect=self.arr_request)
        self.arr_patch.start()

    async def asyncTearDown(self):
        self.arr_patch.stop()
        await fixtures.ServiceTests.asyncTearDown(self)

    @staticmethod
    def server(instance, host, is4k=False, default=True):
        return {'id': instance, 'hostname': host, 'port': 7878, 'apiKey': 'private-arr-key', 'is4k': is4k, 'isDefault': default}

    def arr_request(self, client, method, path, **kwargs):
        self.events.append((method, path, client.config.url, copy.deepcopy(kwargs)))
        if method == 'GET' and path in ('movie', 'series'):
            return copy.deepcopy(self.items)
        if method == 'GET' and path == 'episode':
            return copy.deepcopy(self.episodes)
        if method == 'PUT':
            if self.fail_monitoring:
                raise UserError('private upstream error')
            body = kwargs['json']
            if path == 'movie/editor':
                self.assertEqual(body, {'movieIds': [101], 'monitored': False})
                self.items[0]['monitored'] = False
            elif path == 'series/101':
                self.items[0].update(copy.deepcopy(body))
            elif path == 'episode/monitor':
                for ep in self.episodes:
                    if ep['id'] in body['episodeIds']:
                        ep['monitored'] = body['monitored']
            else:
                self.fail('Unexpected monitoring endpoint ' + path)
            return {}
        self.fail('Unexpected Arr operation ' + method + ' ' + path)

    def tv(self):
        self.kind = 'tv'
        req = fixtures.request()
        req['type'] = 'tv'
        req['media']['tvdbId'] = 20
        self.user_api.get_request.return_value = req
        self.service.api.list_requests.return_value = {'results': [req]}
        self.service.api.get_tv_details.return_value = {'name': 'Series', 'externalIds': {'tvdbId': 20}}
        self.settings = [self.server(0, 'sonarr')]
        self.items = [{'id': 101, 'tvdbId': 20, 'title': 'Series', 'monitored': True, 'monitorNewItems': 'all',
            'statistics': {'episodeFileCount': 4, 'sizeOnDisk': 100},
            'seasons': [{'seasonNumber': 0, 'monitored': True}, {'seasonNumber': 1, 'monitored': True}]}]
        self.episodes = [{'id': i, 'seriesId': 101, 'monitored': True} for i in (1, 2, 3)]

    async def execute(self):
        action_id = await self.schedule()
        await self.make_due(action_id)
        await self.service.process_due_deletions()
        return action_id

    async def test_retained_movie_is_unmonitored_after_confirmed_file_deletion(self):
        action_id = await self.execute()
        self.assertFalse(self.items[0]['monitored'])
        self.assertEqual(self.action(action_id)['status'], 'completed')
        put = next(index for index, event in enumerate(self.events) if event[0] == 'PUT')
        delete = next(index for index, event in enumerate(self.events) if event[0] == 'delete')
        self.assertGreater(put, delete)
        payload = json.loads(self.action(action_id)['payload'])
        self.assertTrue(payload['files_deleted'])
        self.assertNotIn('private-arr-key', json.dumps(payload))
        self.service.api.delete_request.assert_not_called()
        self.assertFalse(any(event[0] == 'DELETE' for event in self.events))

    async def test_absent_arr_entry_is_not_recreated_or_updated(self):
        self.remove_entry = True
        action_id = await self.execute()
        self.assertEqual(self.action(action_id)['status'], 'completed')
        self.assertFalse(any(event[0] in ('PUT', 'POST') for event in self.events))

    async def test_series_seasons_episodes_and_future_seasons_are_unmonitored(self):
        self.tv()
        action_id = await self.execute()
        self.assertEqual(self.action(action_id)['status'], 'completed')
        self.assertFalse(self.items[0]['monitored'])
        self.assertEqual(self.items[0]['monitorNewItems'], 'none')
        self.assertTrue(all(not season['monitored'] for season in self.items[0]['seasons']))
        self.assertTrue(all(not episode['monitored'] for episode in self.episodes))
        self.assertTrue(all(event[2] == 'http://sonarr:7878' for event in self.events if event[0] in ('GET', 'PUT')))

    async def test_only_selected_4k_instance_is_touched(self):
        req = fixtures.request()
        req['is4k'] = True
        req['media'].update(status4k=5, serviceId4k=1)
        self.user_api.get_request.return_value = req
        self.service.api.list_requests.return_value = {'results': [req]}
        action_id = await self.execute()
        self.assertEqual(self.action(action_id)['status'], 'completed')
        self.service.api.delete_media_files.assert_called_once_with(3, True)
        self.assertTrue(all(event[2] == 'http://radarr4k:7878' for event in self.events if event[0] in ('GET', 'PUT')))

    async def test_specific_nondefault_instance_overrides_default(self):
        self.settings.append(self.server(2, 'custom-radarr', default=False))
        self.user_api.get_request.return_value['media']['serviceId'] = 2
        action_id = await self.execute()
        self.assertEqual(self.action(action_id)['status'], 'completed')
        self.assertTrue(all(event[2] == 'http://custom-radarr:7878' for event in self.events if event[0] in ('GET', 'PUT')))

    async def test_failed_monitoring_resumes_after_restart_without_deleting_again(self):
        self.fail_monitoring = True
        action_id = await self.execute()
        self.assertEqual(self.action(action_id)['status'], 'processing')
        self.assertIn('unmonitoring not confirmed', self.action(action_id)['error'])
        self.assertNotIn('private', self.action(action_id)['error'])
        with self.assertRaises(UserError):
            await self.service.cancel_deletion(42, action_id)
        replacement = MediaService(self.config)
        replacement.api.close()
        replacement.api = self.service.api
        replacement.sync.api = replacement.api
        self.service = replacement
        self.fail_monitoring = False
        await self.service.process_due_deletions()
        self.service.api.delete_media_files.assert_called_once()
        self.assertEqual(self.action(action_id)['status'], 'completed')
        self.assertFalse(self.items[0]['monitored'])

    async def test_preflight_outage_preserves_pending_undo_and_never_deletes(self):
        self.fail_settings = True
        action_id = await self.execute()
        self.assertEqual(self.action(action_id)['status'], 'pending')
        self.service.api.delete_media_files.assert_not_called()
        await self.service.cancel_deletion(42, action_id)
        self.assertEqual(self.action(action_id)['status'], 'cancelled')

    async def test_monitoring_destination_change_does_not_redirect_retry(self):
        self.fail_monitoring = True
        action_id = await self.execute()
        self.fail_monitoring = False
        self.settings[0]['hostname'] = 'replacement-radarr'
        before = len(self.events)
        await self.service.process_due_deletions()
        self.assertFalse(any(event[0] == 'PUT' for event in self.events[before:]))
        self.assertEqual(self.action(action_id)['status'], 'processing')
        self.service.api.delete_media_files.assert_called_once()

    async def test_readded_library_entry_is_not_unmonitored_by_old_cleanup(self):
        self.fail_monitoring = True
        action_id = await self.execute()
        self.fail_monitoring = False
        self.items[0]['id'] = 999
        before = len(self.events)
        await self.service.process_due_deletions()
        self.assertFalse(any(event[0] == 'PUT' for event in self.events[before:]))
        self.assertTrue(self.items[0]['monitored'])
        self.assertEqual(self.action(action_id)['status'], 'processing')
        self.service.api.delete_media_files.assert_called_once()

    async def test_revoked_identity_blocks_monitoring_retry(self):
        self.fail_monitoring = True
        action_id = await self.execute()
        self.fail_monitoring = False
        self.user_api.get_notification_settings.return_value = {'discordIds': []}
        before = len(self.events)
        await self.service.process_due_deletions()
        self.assertFalse(any(event[0] == 'PUT' for event in self.events[before:]))
        self.assertEqual(self.action(action_id)['status'], 'processing')
        self.service.api.delete_media_files.assert_called_once()

    async def test_conflicting_tv_provider_mapping_blocks_deletion(self):
        self.tv()
        self.service.api.get_tv_details.return_value['externalIds']['tvdbId'] = 999
        action_id = await self.execute()
        self.assertEqual(self.action(action_id)['status'], 'pending')
        self.service.api.delete_media_files.assert_not_called()
        self.assertFalse(any(event[0] == 'PUT' for event in self.events))

    async def test_changed_owner_blocks_cleanup_without_repeating_deletion(self):
        self.fail_monitoring = True
        action_id = await self.execute()
        self.fail_monitoring = False
        self.user_api.get_request.return_value['requestedBy']['id'] = 999
        before = len(self.events)
        await self.service.process_due_deletions()
        self.assertEqual(self.action(action_id)['status'], 'processing')
        self.assertFalse(any(event[0] == 'PUT' for event in self.events[before:]))
        self.service.api.delete_media_files.assert_called_once()


class MonitoringSafetyTests(unittest.TestCase):
    def test_a_successful_http_response_without_state_change_does_not_complete_cleanup(self):
        client = ArrClient(ArrConfig('radarr', 'http://radarr', 'private'))
        movie = {'id': 1, 'tmdbId': 10, 'monitored': True, 'hasFile': False}
        with patch.object(client, 'request', side_effect=lambda method, path, **kwargs: [movie] if method == 'GET' else {}) as request:
            with self.assertRaises(UserError):
                client.unmonitor_deleted(10, 1)
        self.assertEqual(request.call_count, 3)

    def test_ambiguous_movies_do_not_trigger_writes(self):
        client = ArrClient(ArrConfig('radarr', 'http://radarr', 'private'))
        with patch.object(client, 'request', return_value=[{'id': 1, 'tmdbId': 10}, {'id': 2, 'tmdbId': 10}]) as request:
            with self.assertRaises(UserError):
                client.unmonitor_deleted(10, 1)
        self.assertEqual(request.call_count, 1)

    def test_existing_files_block_unmonitoring_until_confirmed_absent(self):
        client = ArrClient(ArrConfig('radarr', 'http://radarr', 'private'))
        with patch.object(client, 'request', return_value=[{'id': 1, 'tmdbId': 10, 'hasFile': True}]) as request:
            with self.assertRaises(UserError):
                client.unmonitor_deleted(10, 1)
        self.assertEqual(request.call_count, 1)

    def test_instance_zero_cannot_accidentally_delete_from_a_different_default(self):
        from unittest.mock import MagicMock
        api = MagicMock()
        api._request.return_value = [DeletionUnmonitorTests.server(0, 'custom', default=False),
            DeletionUnmonitorTests.server(1, 'default')]
        with self.assertRaises(ValueError):
            deletion_service(api, 'movie', {'tmdbId': 10, 'serviceId': 0}, False)

    def test_missing_tvdb_identity_is_resolved_from_live_seerr_details(self):
        from unittest.mock import MagicMock
        api = MagicMock()
        api._request.return_value = [DeletionUnmonitorTests.server(0, 'sonarr')]
        api.get_tv_details.return_value = {'externalIds': {'tvdbId': 20}}
        source, config, provider_id = deletion_service(api, 'tv', {'tmdbId': 10}, False)
        self.assertEqual((source, config.name, provider_id), ('sonarr:0', 'sonarr', 20))
