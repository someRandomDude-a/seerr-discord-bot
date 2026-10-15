"""Concurrency/freshness regressions use barriers, not fragile stopwatch thresholds."""
import asyncio
import threading
import unittest
from unittest.mock import patch

import test_core as fixtures
from seerr import SeerrAPI
from seerr.concurrency import independent, parallel, parallel_api
from media_bot.security import UserError


class ParallelReadsTests(unittest.TestCase):
    def test_four_reads_overlap_and_results_keep_input_order(self):
        barrier = threading.Barrier(4, timeout=5)
        def read(value):
            barrier.wait()
            return value * 2
        self.assertEqual(parallel(read, range(4)), [0, 2, 4, 6])

    def test_clients_do_not_share_cookies_or_lose_user_scope(self):
        api = SeerrAPI('http://seerr/base', 'private-key', 9, 7)
        try:
            api.session.cookies.set('private-cookie', 'secret')
            with independent(api) as client:
                self.assertIsNot(client.session, api.session)
                self.assertEqual(client.base_url, api.base_url)
                self.assertEqual(client.session.headers['X-API-User'], '7')
                self.assertEqual(client.timeout, 9)
                self.assertFalse(client.session.cookies)
            with self.assertRaises(ValueError):
                cookie = SeerrAPI('http://seerr')
                try:
                    cookie.fork()
                finally:
                    cookie.close()
        finally:
            api.close()

    def test_failure_drains_running_reads_before_returning(self):
        barrier = threading.Barrier(2, timeout=5)
        completed = threading.Event()
        def read(value):
            barrier.wait()
            if value == 0:
                raise RuntimeError('failed')
            completed.set()
        with self.assertRaises(RuntimeError):
            parallel(read, [0, 1])
        self.assertTrue(completed.is_set())

    def test_parallel_metadata_reuses_one_session_per_worker_and_closes_after_drain(self):
        api = SeerrAPI('http://seerr', 'key', 15, 7)
        barrier = threading.Barrier(4, timeout=5)
        clients = []
        closed = set()
        lock = threading.Lock()
        fork = api.fork
        def tracked_fork():
            client = fork()
            close = client.close
            def tracked_close():
                with lock:
                    closed.add(id(client))
                close()
            client.close = tracked_close
            with lock:
                clients.append(client)
            return client
        def read(client):
            barrier.wait()
            with lock:
                self.assertNotIn(id(client), closed)
            return id(client)
        try:
            with patch.object(api, 'fork', side_effect=tracked_fork):
                sessions = parallel_api(api, [read] * 12)
            self.assertEqual(len(set(sessions)), 4)
            self.assertEqual(len(clients), 4)
            self.assertEqual(closed, set(sessions))
        finally:
            api.close()

    def test_empty_parallel_reads_do_not_open_a_session(self):
        api = SeerrAPI('http://seerr', 'key')
        try:
            with patch.object(api, 'fork') as fork:
                self.assertEqual(parallel_api(api, []), [])
            fork.assert_not_called()
        finally:
            api.close()


class ServicePerformanceTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        await fixtures.ServiceTests.asyncSetUp(self)

    async def asyncTearDown(self):
        await fixtures.ServiceTests.asyncTearDown(self)

    async def test_snapshot_sources_overlap_but_commit_only_after_all_succeed(self):
        self.config.seerr_discovery = True
        barrier = threading.Barrier(4, timeout=5)
        def stage(value):
            barrier.wait()
            return value
        with patch.object(self.service.sync, 'sync', side_effect=lambda: stage(True)), \
                patch('media_bot.service.discover_arr', side_effect=lambda api: stage({})), \
                patch.object(self.service, '_seerr_catalog', side_effect=lambda: stage({'items': [], 'disks': [], 'tv_identities': {}})), \
                patch.object(self.service, '_watched_states', side_effect=lambda: stage([])):
            await self.service.refresh()
        self.assertEqual(self.service.refresh_revision, 1)
        self.assertEqual(self.service.store.meta()['error'], '')
        with self.service.store.connect() as db:
            original = db.execute('SELECT payload FROM snapshots').fetchone()[0]
        with patch.object(self.service, '_watched_states', side_effect=RuntimeError('offline')):
            with self.assertRaises(UserError):
                await self.service.refresh()
        self.assertEqual(self.service.refresh_revision, 1)
        self.assertTrue(self.service.store.meta()['error'])
        with self.service.store.connect() as db:
            self.assertEqual(db.execute('SELECT payload FROM snapshots').fetchone()[0], original)

    async def test_overlapping_browse_calls_share_only_a_refresh_completed_after_they_started(self):
        started, release = threading.Event(), threading.Event()
        def refresh():
            started.set()
            if not release.wait(5):
                raise RuntimeError('test timeout')
            self.service.refresh_revision += 1
        with patch.object(self.service, '_refresh', side_effect=refresh) as refresh_mock:
            first = asyncio.create_task(self.service.run(42, 'requests', False))
            await asyncio.to_thread(started.wait, 5)
            second = asyncio.create_task(self.service.run(42, 'requests', False))
            await asyncio.sleep(0)
            release.set()
            await asyncio.gather(first, second)
            self.assertEqual(refresh_mock.call_count, 1)
            await self.service.run(42, 'requests', False)
            self.assertEqual(refresh_mock.call_count, 2)  # No timed snapshot fallback.

    async def test_preferences_do_not_wait_for_unrelated_snapshot_writers(self):
        async with self.service.lock:
            bundle = await asyncio.wait_for(self.service.mutate(42, 'preferences', True, None), 5)
        self.assertTrue(bundle['viewer']['account']['opted_in'])
        self.assertEqual(bundle['result'], 'Preferences saved.')

    async def test_web_movie_request_returns_confirmed_state_without_unrelated_global_sync(self):
        self.user_api.get_movie_details.return_value = {'title': 'Film', 'mediaInfo': {'status': 1}}
        self.user_api.create_request.return_value = {'id': 23}
        item = {'kind': 'movie', 'external_id': '10', 'title': 'Film'}
        with patch.object(self.service, '_refresh') as refresh:
            bundle = await self.service.mutate(42, 'request', item)
        refresh.assert_not_called()
        self.assertIn('#23 submitted', bundle['result'])
        self.assertFalse(bundle['item']['requestable'])
        self.assertTrue(self.service.store.meta()['error'])  # Other snapshots remain blocked pending sync.
        self.user_api.create_request.assert_called_once_with('movie', 10)

    async def test_missing_metadata_is_parallel_and_duplicate_media_is_fetched_once(self):
        requests = []
        for index in [0, 1, 2, 0]:
            row = fixtures.request()
            row['id'] = len(requests) + 1
            row['media'] = {**row['media'], 'id': index + 100, 'tmdbId': index + 10}
            requests.append(row)
        self.service.api.list_requests.return_value = {'results': requests}
        barrier = threading.Barrier(3, timeout=5)
        def details(tmdb):
            barrier.wait()
            return {'title': f'Film {tmdb}'}
        self.service.api.get_movie_details.side_effect = details
        await self.service.offload(self.service.sync.sync)
        self.assertEqual(self.service.api.get_movie_details.call_count, 3)
        with self.service.store.connect() as db:
            self.assertEqual(db.execute('SELECT COUNT(*) FROM requests').fetchone()[0], 4)

    async def test_cancellation_keeps_the_writer_lock_until_parallel_workers_finish(self):
        started, release = threading.Event(), threading.Event()
        def stage():
            started.set()
            if not release.wait(5):
                raise RuntimeError('test timeout')
            return []
        with patch.object(self.service, '_watched_states', side_effect=stage):
            task = asyncio.create_task(self.service.refresh())
            await asyncio.to_thread(started.wait, 5)
            task.cancel()
            await asyncio.sleep(0)
            self.assertTrue(self.service.lock.locked())
            release.set()
            with self.assertRaises(asyncio.CancelledError):
                await task
            self.assertFalse(self.service.lock.locked())
