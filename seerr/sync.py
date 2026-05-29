import sqlite3
import json
import hashlib
import time
import threading
from datetime import datetime, timezone
from seerr import SeerrAPI, SeerrAPIError

class SyncManager:
    def __init__(self, api: SeerrAPI, db_path: str = "seerr_cache.db"):
        self.api = api
        self.db_path = db_path
        self._stop_event = threading.Event()
        self._init_db()

    # Single DB connection manager
    def _connect(self):
        conn = sqlite3.connect(self.db_path, timeout=10)
        conn.execute("PRAGMA journal_mode=WAL")
        return conn

    # Initialise tables
    def _init_db(self):
        with self._connect() as conn:
            conn.executescript("""
                CREATE TABLE IF NOT EXISTS requests (
                    id INTEGER PRIMARY KEY,
                    type TEXT,
                    status INTEGER,
                    media_id INTEGER,
                    tmdb_id INTEGER,
                    title TEXT,
                    poster_path TEXT,
                    requested_by_id INTEGER,
                    requested_by_name TEXT,
                    is4k INTEGER,
                    created_at TEXT,
                    updated_at TEXT
                );
                CREATE TABLE IF NOT EXISTS media_details (
                    media_id INTEGER PRIMARY KEY,
                    type TEXT,
                    tmdb_id INTEGER,
                    title TEXT,
                    poster_path TEXT,
                    backdrop_path TEXT,
                    overview TEXT,
                    release_date TEXT
                );
                CREATE TABLE IF NOT EXISTS sync_meta (
                    key TEXT PRIMARY KEY,
                    value TEXT
                );
            """)
            conn.commit()

    # Paginated fetch 
    def _fetch_all_requests(self):
        all_requests = []
        take = 50
        skip = 0
        while True:
            try:
                response = self.api.list_requests(take=take, skip=skip)
                results = response.get("results", [])
                all_requests.extend(results)
                if len(results) < take:
                    break
                skip += take
            except SeerrAPIError as e:
                print(f"Error fetching page (skip={skip}): {e}")
                break
        return all_requests

    # Hash functions
    def _compute_hash(self, requests):
        sorted_list = sorted(requests, key=lambda r: r["id"])
        data = json.dumps(sorted_list, sort_keys=True, default=str)
        return hashlib.sha256(data.encode()).hexdigest()

    def _get_stored_hash(self, conn):
        row = conn.execute(
            "SELECT value FROM sync_meta WHERE key = 'hash'"
        ).fetchone()
        return row[0] if row else ""

    def _store_hash(self, conn, hash_val):
        """No commit here, caller will commit"""
        conn.execute(
            "INSERT OR REPLACE INTO sync_meta (key, value) VALUES ('hash', ?)",
            (hash_val,)
        )
        conn.execute(
            "INSERT OR REPLACE INTO sync_meta (key, value) VALUES ('last_sync', ?)",
            (datetime.now(timezone.utc).isoformat(),)
        )

    # TMDB fetch with exponential backoff
    def _fetch_media_details_with_retry(self, tmdb_id, media_type, max_retries=5):
        for attempt in range(max_retries):
            try:
                if media_type == "movie":
                    data = self.api.get_movie_details(tmdb_id)
                else:
                    data = self.api.get_tv_details(tmdb_id)
                return {
                    "tmdb_id": data.get("id") or data.get("externalId"),
                    "title": data.get("title") or data.get("name") or "Unknown",
                    "poster_path": data.get("posterPath", ""),
                    "backdrop_path": data.get("backdropPath", ""),
                    "overview": data.get("overview", ""),
                    "release_date": data.get("releaseDate") or data.get("firstAirDate", "")
                }
            except SeerrAPIError:
                if attempt == max_retries - 1:
                    raise
                wait = 2 ** attempt
                print(f"TMDB fetch failed (attempt {attempt+1}/{max_retries}), retrying in {wait}s...")
                time.sleep(wait)

    # Enrich media cache
    def _enrich_media_cache(self, conn, requests):
        for req in requests:
            media = req.get("media", {})
            media_id = media.get("id")
            tmdb_id = media.get("tmdbId")
            media_type = req["type"]
            if not media_id or not tmdb_id:
                continue

            # Already cached?
            exists = conn.execute(
                "SELECT 1 FROM media_details WHERE media_id = ?", (media_id,)
            ).fetchone()
            if exists:
                continue

            # Fetch details or insert placeholder
            try:
                details = self._fetch_media_details_with_retry(tmdb_id, media_type)
            except SeerrAPIError as e:
                print(f"Failed to fetch details for media {media_id}: {e}")
                details = {
                    "tmdb_id": tmdb_id,
                    "title": media.get("externalServiceSlug") or "Unknown",
                    "poster_path": "",
                    "backdrop_path": "",
                    "overview": "",
                    "release_date": ""
                }

            conn.execute("""
                INSERT OR REPLACE INTO media_details
                (media_id, type, tmdb_id, title, poster_path, backdrop_path, overview, release_date)
                VALUES (?,?,?,?,?,?,?,?)
            """, (
                media_id, media_type, details["tmdb_id"],
                details["title"], details["poster_path"],
                details["backdrop_path"], details["overview"],
                details["release_date"]
            ))
            conn.execute("""
                UPDATE requests
                SET title = ?, poster_path = ?
                WHERE media_id = ?
            """, (details["title"], details["poster_path"], media_id))

    # Main sync
    def sync(self):
        all_requests = self._fetch_all_requests()
        if not all_requests:
            print("No requests fetched.")
            return False

        new_hash = self._compute_hash(all_requests)

        # Open connection for the entire sync cycle
        with self._connect() as conn:
            old_hash = self._get_stored_hash(conn)
            if new_hash == old_hash:
                print("No changes detected.")
                return False

            print("Changes detected. Updating local database...")
            fetched_ids = set()

            for req in all_requests:
                rid = req["id"]
                fetched_ids.add(rid)
                media = req.get("media", {})
                user = req.get("requestedBy", {})
                temp_title = media.get("externalServiceSlug") or media.get("title") or "Unknown"

                existing = conn.execute(
                    "SELECT updated_at FROM requests WHERE id = ?", (rid,)
                ).fetchone()

                if existing is None:
                    conn.execute("""
                        INSERT INTO requests (
                            id, type, status, media_id, tmdb_id, title, poster_path,
                            requested_by_id, requested_by_name, is4k,
                            created_at, updated_at
                        ) VALUES (?,?,?,?,?,?,?,?,?,?,?,?)
                    """, (
                        rid, req["type"], req["status"],
                        media.get("id"), media.get("tmdbId"),
                        temp_title, "",
                        user.get("id"), user.get("jellyfinUsername"),
                        int(req.get("is4k", False)),
                        req["createdAt"], req["updatedAt"]
                    ))
                elif req["updatedAt"] > existing[0]:
                    conn.execute("""
                        UPDATE requests SET
                            type = ?, status = ?, media_id = ?, tmdb_id = ?,
                            title = ?, requested_by_id = ?, requested_by_name = ?,
                            is4k = ?, created_at = ?, updated_at = ?
                        WHERE id = ?
                    """, (
                        req["type"], req["status"],
                        media.get("id"), media.get("tmdbId"),
                        temp_title,
                        user.get("id"), user.get("jellyfinUsername"),
                        int(req.get("is4k", False)),
                        req["createdAt"], req["updatedAt"],
                        rid
                    ))

            # Delete requests no longer on server
            if fetched_ids:
                placeholders = ','.join('?' * len(fetched_ids))
                conn.execute(
                    f"DELETE FROM requests WHERE id NOT IN ({placeholders})",
                    list(fetched_ids)
                )

            # Store hash and enrich media
            self._store_hash(conn, new_hash)
            self._enrich_media_cache(conn, all_requests)

            # One final commit for everything
            conn.commit()

        print("Sync complete.")
        return True

    # Background loop
    def start_loop(self, interval=3600):
        def loop():
            self.sync()   # run once immediately
            while not self._stop_event.is_set():
                self._stop_event.wait(interval)
                self.sync()
        thread = threading.Thread(target=loop, daemon=True)
        thread.start()
        print(f"Sync loop started (interval: {interval}s).")

    def stop_loop(self):
        self._stop_event.set()