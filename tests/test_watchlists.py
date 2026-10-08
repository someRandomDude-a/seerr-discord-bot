import tempfile
import unittest
from pathlib import Path
from unittest.mock import MagicMock

from media_bot.security import UserError, VerificationRequired
from media_bot.store import Store
from media_bot.watchlists import WatchlistSync


class WatchlistAPI:
    """Native Seerr contract with user-scoped membership and full pagination."""
    def __init__(self, user=7, items=None):
        self.user = user
        self.items = dict(items or {})
        self.calls = []
        self.fail_read = False
        self.fail_write = False
        self.commit_then_timeout = False

    def _request(self, method, endpoint, json=None, params=None):
        self.calls.append((method, endpoint, json, params))
        if method == 'GET' and endpoint == '/discover/watchlist':
            if self.fail_read:
                raise RuntimeError('private-host and credential')
            page = params['page']
            rows = [{'mediaType': kind, 'tmdbId': int(eid), 'title': title, 'requestedBy': {'id': self.user}}
                    for (kind, eid), title in sorted(self.items.items())]
            return {'page': page, 'totalPages': max(1, (len(rows) + 19) // 20), 'totalResults': len(rows),
                    'results': rows[(page - 1) * 20:page * 20]}
        if self.fail_write:
            raise RuntimeError('upstream rejected write')
        if method == 'POST' and endpoint == '/watchlist':
            self.items[(json['mediaType'], str(json['tmdbId']))] = json['title']
        elif method == 'DELETE' and endpoint.startswith('/watchlist/'):
            self.items.pop((params['mediaType'], endpoint.rsplit('/', 1)[1]), None)
        else:
            raise AssertionError((method, endpoint))
        if self.commit_then_timeout:
            raise TimeoutError('result lost')

    def writes(self):
        return [call for call in self.calls if call[0] != 'GET']


class WatchlistSyncTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.path = str(Path(self.tmp.name) / 'cache.db')
        self.store = Store(self.path)
        self.store.link(42, 7, 'Viewer')
        self.account = self.store.account(42)
        self.syncer = WatchlistSync(self.store, 'http://seerr')
        self.api = WatchlistAPI()
        self.verify = MagicMock()

    def tearDown(self):
        self.tmp.cleanup()

    def sync(self):
        return self.syncer.sync(self.account, self.api, self.verify)

    def local(self):
        return {(row['kind'], row['external_id']): row['title'] for row in self.store.watches(42)}

    def pending(self):
        with self.store.connect() as db:
            return [dict(row) for row in db.execute('SELECT * FROM watchlist_intents WHERE discord_id=\'42\'')]

    def legacy(self, kind, eid, title):
        with self.store.connect() as db:
            db.execute('INSERT INTO watches VALUES (?,?,?,?)', ('42', kind, str(eid), title))

    def test_initial_union_preserves_both_lists_and_never_requests_media(self):
        self.legacy('movie', 10, 'Local film')
        self.api.items = {('tv', '20'): 'Remote series'}
        self.store.watch(42, 'music', 'album-id', 'Album')
        self.sync()
        self.assertEqual(set(self.local()), {('movie', '10'), ('tv', '20'), ('music', 'album-id')})
        self.assertEqual(set(self.api.items), {('movie', '10'), ('tv', '20')})
        self.assertEqual(self.pending(), [])
        self.assertEqual(self.api.writes(), [('POST', '/watchlist', {'mediaType': 'movie', 'tmdbId': 10, 'title': 'Local film'}, None)])
        self.verify.assert_called_once()

    def test_remote_additions_and_removals_converge_without_resurrection(self):
        self.api.items = {('movie', '10'): 'Film'}
        self.sync()
        self.api.items = {('tv', '20'): 'Series'}
        self.sync()
        self.assertEqual(self.local(), self.api.items)
        self.assertEqual(self.api.writes(), [])
        self.sync()
        self.assertEqual(set(self.local()), {('tv', '20')})

    def test_local_additions_and_removals_are_written_through(self):
        self.sync()
        self.store.watch(42, 'movie', '10', 'Film')
        self.sync()
        self.store.watch(42, 'movie', '10', 'Film', remove=True)
        self.sync()
        self.assertEqual(self.local(), {})
        self.assertEqual(self.api.items, {})
        self.assertEqual(self.pending(), [])
        self.assertEqual(self.api.writes()[-1], ('DELETE', '/watchlist/10', None, {'mediaType': 'movie'}))

    def test_movie_and_tv_same_tmdb_id_are_distinct(self):
        self.store.watch(42, 'movie', 10, 'Movie')
        self.store.watch(42, 'tv', 10, 'TV')
        self.sync()
        self.store.watch(42, 'tv', 10, 'TV', remove=True)
        self.sync()
        self.assertEqual(set(self.api.items), {('movie', '10')})

    def test_pending_removal_before_first_sync_overrides_initial_union(self):
        self.api.items = {('movie', '10'): 'Remote film'}
        self.store.watch(42, 'movie', 10, 'Film', remove=True)
        self.sync()
        self.assertEqual(self.local(), {})
        self.assertEqual(self.api.items, {})

    def test_outage_preserves_baseline_and_intent_across_restart(self):
        self.api.items = {('movie', '10'): 'Film'}
        self.sync()
        self.store.watch(42, 'movie', 10, 'Film', remove=True)
        self.api.fail_read = True
        with self.assertRaises(RuntimeError):
            self.sync()
        self.assertEqual(self.local(), {})
        self.assertEqual(len(self.pending()), 1)
        self.store = Store(self.path)
        self.syncer = WatchlistSync(self.store, 'http://seerr')
        self.api.fail_read = False
        self.sync()
        self.assertEqual(self.api.items, {})
        self.assertEqual(self.pending(), [])
        with self.store.connect() as db:
            state = db.execute('SELECT * FROM watchlist_sync').fetchone()
        self.assertTrue(state['last_success'])
        self.assertEqual(state['error'], '')

    def test_failed_write_is_not_echoed_back_and_recovery_uses_fresh_read(self):
        self.sync()
        self.store.watch(42, 'tv', 20, 'Series')
        self.api.fail_write = True
        with self.assertRaises(UserError):
            self.sync()
        self.assertIn(('tv', '20'), self.local())
        self.assertEqual(len(self.pending()), 1)
        self.api.fail_write = False
        self.sync()
        self.assertEqual(self.local(), self.api.items)
        self.assertEqual(self.pending(), [])

    def test_timeout_after_success_is_resolved_without_duplicate_write(self):
        self.store.watch(42, 'movie', 10, 'Film')
        self.api.commit_then_timeout = True
        self.sync()
        self.assertEqual(self.pending(), [])
        self.sync()
        self.assertEqual(len(self.api.writes()), 1)

    def test_failed_delete_never_readds_remote_entry_locally(self):
        self.api.items = {('movie', '10'): 'Film'}
        self.sync()
        self.store.watch(42, 'movie', 10, 'Film', remove=True)
        self.api.fail_write = True
        for _ in range(2):
            with self.assertRaises(UserError):
                self.sync()
            self.assertEqual(self.local(), {})
        self.api.fail_write = False
        self.sync()
        self.assertEqual(self.api.items, {})

    def test_latest_explicit_local_choice_wins_conflict(self):
        self.api.items = {('movie', '10'): 'Film'}
        self.sync()
        del self.api.items[('movie', '10')]
        self.store.watch(42, 'movie', 10, 'Film')
        self.sync()
        self.assertIn(('movie', '10'), self.api.items)
        self.store.watch(42, 'movie', 10, 'Film', remove=True)
        self.store.watch(42, 'movie', 10, 'Film')
        self.sync()
        self.assertIn(('movie', '10'), self.local())

    def test_music_and_books_are_never_sent_to_seerr(self):
        self.store.watch(42, 'music', 'album-id', 'Album')
        self.store.watch(42, 'book', 'book-id', 'Book')
        self.sync()
        self.assertEqual(len(self.local()), 2)
        self.assertEqual(self.api.items, {})
        self.assertEqual(self.api.writes(), [])

    def test_pagination_reads_all_items_and_unchanged_sync_does_not_write(self):
        self.api.items = {('movie', str(i)): f'Film {i}' for i in range(1, 47)}
        self.sync()
        self.assertEqual(len(self.local()), 46)
        self.assertEqual([call[3]['page'] for call in self.api.calls], [1, 2, 3, 1, 2, 3])
        self.sync()
        self.assertEqual(self.api.writes(), [])

    def test_invalid_incomplete_or_other_users_response_never_erases_local_entries(self):
        self.legacy('movie', 10, 'Film')
        bad = [None, {'results': []}, {'page': 1, 'totalPages': 1, 'totalResults': 1, 'results': []},
               {'page': 1, 'totalPages': 1, 'totalResults': 1, 'results': [{'mediaType': 'tv', 'tmdbId': 20, 'title': 'Private', 'requestedBy': {'id': 999}}]},
               {'page': 1, 'totalPages': 1, 'totalResults': 1, 'results': [{'mediaType': 'tv', 'tmdbId': None, 'title': 'Broken'}]}]
        original = self.api._request
        for response in bad:
            self.api._request = MagicMock(return_value=response)
            with self.assertRaises(UserError):
                self.sync()
            self.assertEqual(self.local(), {('movie', '10'): 'Film'})
            self.assertFalse(self.api._request.call_args.args[0] != 'GET')
        self.api._request = original

    def test_inconsistent_scan_and_duplicate_pagination_are_rejected_before_writes(self):
        self.legacy('movie', 10, 'Film')
        empty = {'page': 1, 'totalPages': 1, 'totalResults': 0, 'results': []}
        full = {'page': 1, 'totalPages': 1, 'totalResults': 1, 'results': [{'mediaType': 'tv', 'tmdbId': 20, 'title': 'Series'}]}
        self.api._request = MagicMock(side_effect=[empty, full])
        with self.assertRaises(UserError):
            self.sync()
        self.assertEqual(len(self.local()), 1)
        duplicates = dict(full, totalResults=2, results=full['results'] * 2)
        self.api._request = MagicMock(return_value=duplicates)
        with self.assertRaises(UserError):
            self.sync()

    def test_over_limit_initial_merge_preserves_lists_and_makes_no_writes(self):
        self.legacy('movie', 999, 'Local')
        self.api.items = {('tv', str(i)): 'Series' for i in range(1, 201)}
        with self.assertRaisesRegex(UserError, '200'):
            self.sync()
        self.assertEqual(len(self.local()), 1)
        self.assertEqual(len(self.api.items), 200)
        self.assertEqual(self.api.writes(), [])

    def test_destination_or_account_switch_cannot_forward_pending_entries(self):
        self.sync()
        self.store.watch(42, 'movie', 10, 'Film')
        for syncer, account in [(WatchlistSync(self.store, 'http://other-seerr'), self.account),
                                (self.syncer, dict(self.account, seerr_id=999))]:
            with self.assertRaisesRegex(UserError, 'destination'):
                syncer.sync(account, self.api, self.verify)
        self.assertEqual(self.api.writes(), [])
        self.assertEqual(len(self.pending()), 1)

    def test_revocation_before_write_keeps_intent_and_sends_nothing(self):
        self.store.watch(42, 'movie', 10, 'Film')
        self.verify.side_effect = VerificationRequired('Run /link again.')
        with self.assertRaises(VerificationRequired):
            self.sync()
        self.assertEqual(self.api.writes(), [])
        self.assertEqual(len(self.pending()), 1)

    def test_user_scoping_does_not_import_or_remove_another_accounts_watches(self):
        self.store.link(99, 8, 'Other viewer')
        self.store.watch(99, 'movie', 99, 'Other film')
        self.api.items = {('movie', '10'): 'Film'}
        self.sync()
        self.assertEqual(set(self.local()), {('movie', '10')})
        self.assertEqual(self.store.watches(99)[0]['external_id'], '99')

    def test_remote_changes_during_write_are_not_reported_as_converged(self):
        self.store.watch(42, 'movie', 10, 'Film')
        original = self.api._request
        def changed(method, endpoint, **kwargs):
            result = original(method, endpoint, **kwargs)
            if method == 'POST':
                self.api.items[('tv', '20')] = 'New remote series'
            return result
        self.api._request = changed
        with self.assertRaises(UserError):
            self.sync()
        self.sync()
        self.assertEqual(self.local(), self.api.items)

    def test_newer_intent_during_write_is_not_acknowledged_by_old_plan(self):
        self.store.watch(42, 'movie', 10, 'Film')
        def toggle():
            self.store.watch(42, 'movie', 10, 'Film', remove=True)
        self.verify.side_effect = toggle
        with self.assertRaises(UserError):
            self.sync()
        self.assertEqual(self.pending()[0]['desired'], 0)
        self.verify.side_effect = None
        self.sync()
        self.assertEqual(self.local(), {})
        self.assertEqual(self.api.items, {})
