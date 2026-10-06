"""Atomic request snapshots. Failed/partial fetches never replace a good cache."""
import hashlib
import json
import logging
import sqlite3
import threading
from contextlib import contextmanager
from datetime import datetime, timezone


class SyncManager:
    def __init__(self, api, db_path="seerr_cache.db"):
        self.api = api
        self.db_path = db_path
        self._lock = threading.Lock()
        self._stop_event = threading.Event()
        with self._connect() as conn:
            conn.executescript("""
                CREATE TABLE IF NOT EXISTS requests (
                    id INTEGER PRIMARY KEY, type TEXT, status INTEGER,
                    media_id INTEGER, tmdb_id INTEGER, title TEXT, poster_path TEXT,
                    requested_by_id INTEGER, requested_by_name TEXT, is4k INTEGER,
                    created_at TEXT, updated_at TEXT
                );
                CREATE TABLE IF NOT EXISTS media_details (
                    media_id INTEGER PRIMARY KEY, type TEXT, tmdb_id INTEGER,
                    title TEXT, poster_path TEXT, backdrop_path TEXT,
                    overview TEXT, release_date TEXT
                );
                CREATE TABLE IF NOT EXISTS sync_meta (key TEXT PRIMARY KEY, value TEXT);
            """)
            columns = {row[1] for row in conn.execute('PRAGMA table_info(requests)')}
            for column, kind in [('media_status', 'INTEGER'), ('jellyfin_id', 'TEXT'), ('raw_json', 'TEXT')]:
                if column not in columns:
                    conn.execute(f'ALTER TABLE requests ADD COLUMN {column} {kind}')

    @contextmanager
    def _connect(self):
        conn = sqlite3.connect(self.db_path, timeout=30)
        conn.execute('PRAGMA journal_mode=WAL')
        try:
            with conn:
                yield conn
        finally:
            conn.close()

    def _fetch_all_requests(self):
        results, skip, expected = [], 0, None
        while True:
            page = self.api.list_requests(take=50, skip=skip, sort='added')
            batch = page['results']  # A malformed or failed page aborts the entire cycle.
            count = (page.get('pageInfo') or {}).get('results')
            if count is not None:
                if expected is not None and expected != count:
                    raise RuntimeError('Request count changed during sync')
                expected = count
            results.extend(batch)
            if len(batch) < 50:
                break
            skip += 50
        if len({r['id'] for r in results}) != len(results):
            raise RuntimeError('Request pagination changed during sync; retry required')
        if expected is not None and len(results) != expected:
            raise RuntimeError('Incomplete request pagination')
        return results

    def sync(self):
        with self._lock:
            try:
                requests = self._fetch_all_requests()
                digest = hashlib.sha256(json.dumps(sorted(requests, key=lambda r: r['id']), sort_keys=True).encode()).hexdigest()
                rows, new_details = [], []
                with self._connect() as conn:
                    cached_details = {r[0]: (r[1], r[2]) for r in conn.execute('SELECT media_id,title,poster_path FROM media_details')}
                # Never hold a SQLite write lock while waiting for metadata HTTP calls.
                for req in requests:
                    media, user = req.get('media') or {}, req.get('requestedBy') or {}
                    cached = cached_details.get(media.get('id'))
                    if cached:
                        title, poster = cached
                    else:
                        details = (self.api.get_movie_details(media['tmdbId']) if req['type'] == 'movie'
                                   else self.api.get_tv_details(media['tmdbId']))
                        title = details.get('title') or details.get('name') or 'Untitled'
                        poster = details.get('posterPath') or ''
                        cached_details[media['id']] = (title, poster)
                        new_details.append((media['id'], req['type'], media['tmdbId'], title, poster))
                    rows.append((req['id'], req['type'], req['status'], media.get('id'), media.get('tmdbId'),
                                 title, poster, user.get('id'), user.get('displayName') or user.get('jellyfinUsername'),
                                 int(req.get('is4k', False)), req.get('createdAt'), req.get('updatedAt'),
                                 media.get('status4k') if req.get('is4k') else media.get('status'),
                                 media.get('jellyfinMediaId4k') if req.get('is4k') else media.get('jellyfinMediaId'), json.dumps(req)))
                with self._connect() as conn:
                    conn.executemany('INSERT OR REPLACE INTO media_details (media_id,type,tmdb_id,title,poster_path) VALUES (?,?,?,?,?)', new_details)
                    # Only the local snapshot is replaced; no remote requests are deleted.
                    conn.execute('DELETE FROM requests')
                    conn.executemany('INSERT INTO requests VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)', rows)
                    for key, value in [('hash', digest), ('last_sync', datetime.now(timezone.utc).isoformat()), ('error', '')]:
                        conn.execute('INSERT OR REPLACE INTO sync_meta VALUES (?,?)', (key, value))
                return True
            except Exception:
                with self._connect() as conn:
                    conn.execute("INSERT OR REPLACE INTO sync_meta VALUES ('error', 'Sync failed; cached data cannot be used')")
                raise

    def start_loop(self, interval=300):
        def loop():
            while not self._stop_event.is_set():
                try:
                    self.sync()
                except Exception:
                    logging.getLogger(__name__).warning('Request sync failed; retaining previous snapshot')
                self._stop_event.wait(interval)
        self._thread = threading.Thread(target=loop, daemon=True)
        self._thread.start()

    def stop_loop(self):
        self._stop_event.set()
