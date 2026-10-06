import json
import sqlite3
import time
from contextlib import contextmanager


class Store:
    def __init__(self, path):
        self.path = path
        with self.connect() as db:
            db.executescript('''
                CREATE TABLE IF NOT EXISTS accounts (
                    discord_id TEXT PRIMARY KEY, seerr_id INTEGER UNIQUE NOT NULL,
                    name TEXT NOT NULL, opted_in INTEGER NOT NULL DEFAULT 0,
                    device TEXT, linked_at REAL NOT NULL
                );
                CREATE TABLE IF NOT EXISTS watches (
                    discord_id TEXT NOT NULL, kind TEXT NOT NULL, external_id TEXT NOT NULL,
                    title TEXT NOT NULL, PRIMARY KEY(discord_id, kind, external_id)
                );
                CREATE TABLE IF NOT EXISTS actions (
                    id INTEGER PRIMARY KEY, discord_id TEXT NOT NULL, action TEXT NOT NULL,
                    kind TEXT NOT NULL, external_id TEXT NOT NULL, title TEXT NOT NULL,
                    payload TEXT NOT NULL, status TEXT NOT NULL DEFAULT 'pending',
                    created_at REAL NOT NULL, reviewed_by TEXT, error TEXT, execute_after REAL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS pending_action
                    ON actions(action, kind, external_id) WHERE status IN ('pending', 'processing', 'uncertain');
                CREATE TABLE IF NOT EXISTS snapshots (
                    source TEXT PRIMARY KEY, payload TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS hub_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS observed (
                    kind TEXT NOT NULL, external_id TEXT NOT NULL, state TEXT NOT NULL,
                    revision INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(kind, external_id)
                );
                CREATE TABLE IF NOT EXISTS outbox (
                    id INTEGER PRIMARY KEY, discord_id TEXT NOT NULL, event_key TEXT NOT NULL,
                    title TEXT NOT NULL, body TEXT NOT NULL, sent INTEGER NOT NULL DEFAULT 0,
                    attempts INTEGER NOT NULL DEFAULT 0, next_attempt REAL NOT NULL DEFAULT 0,
                    UNIQUE(discord_id, event_key)
                );
                CREATE TABLE IF NOT EXISTS inbox (
                    id INTEGER PRIMARY KEY, message_id TEXT UNIQUE NOT NULL,
                    author_id TEXT NOT NULL, author_name TEXT NOT NULL,
                    guild_id TEXT, guild_name TEXT, channel_id TEXT NOT NULL,
                    channel_name TEXT, content TEXT NOT NULL, attachment_count INTEGER NOT NULL,
                    created_at REAL NOT NULL
                );
                CREATE TABLE IF NOT EXISTS announcements (
                    id INTEGER PRIMARY KEY, actor_id TEXT NOT NULL,
                    content TEXT NOT NULL, created_at REAL NOT NULL
                );
                CREATE INDEX IF NOT EXISTS inbox_time ON inbox(created_at);
                CREATE TABLE IF NOT EXISTS announcement_deliveries (
                    id INTEGER PRIMARY KEY, announcement_id INTEGER NOT NULL,
                    kind TEXT NOT NULL, target_id TEXT NOT NULL,
                    label TEXT NOT NULL, status TEXT NOT NULL DEFAULT 'pending',
                    message_id TEXT, error TEXT,
                    UNIQUE(announcement_id, kind, target_id)
                );
            ''')
            columns = {r['name'] for r in db.execute('PRAGMA table_info(actions)')}
            if 'execute_after' not in columns:
                db.execute('ALTER TABLE actions ADD COLUMN execute_after REAL')

    @contextmanager
    def connect(self):
        db = sqlite3.connect(self.path, timeout=30)
        db.row_factory = sqlite3.Row
        db.execute('PRAGMA journal_mode=WAL')
        try:
            with db:
                yield db
        finally:
            db.close()

    def account(self, discord_id):
        with self.connect() as db:
            row = db.execute('SELECT * FROM accounts WHERE discord_id=?', (str(discord_id),)).fetchone()
            return dict(row) if row else None

    def link(self, discord_id, seerr_id, name):
        with self.connect() as db:
            db.execute('''INSERT INTO accounts(discord_id,seerr_id,name,linked_at) VALUES (?,?,?,?)
                ON CONFLICT(discord_id) DO UPDATE SET seerr_id=excluded.seerr_id,
                name=excluded.name, linked_at=excluded.linked_at''', (str(discord_id), seerr_id, name, time.time()))

    def preference(self, discord_id, *, opted_in=None, device=None):
        with self.connect() as db:
            if opted_in is not None:
                db.execute('UPDATE accounts SET opted_in=? WHERE discord_id=?', (int(opted_in), str(discord_id)))
                if not opted_in:
                    db.execute('UPDATE outbox SET sent=1 WHERE discord_id=?', (str(discord_id),))
            if device is not None:
                db.execute('UPDATE accounts SET device=? WHERE discord_id=?', (device, str(discord_id)))

    def watch(self, discord_id, kind, external_id, title, remove=False):
        with self.connect() as db:
            if remove:
                db.execute('DELETE FROM watches WHERE discord_id=? AND kind=? AND external_id=?',
                           (str(discord_id), kind, str(external_id)))
            else:
                count = db.execute('SELECT COUNT(*) FROM watches WHERE discord_id=?', (str(discord_id),)).fetchone()[0]
                existing = db.execute('SELECT 1 FROM watches WHERE discord_id=? AND kind=? AND external_id=?',
                                      (str(discord_id), kind, str(external_id))).fetchone()
                if count >= 200 and not existing:
                    from .security import UserError
                    raise UserError('Watchlist limit reached (200 items). Remove an item first.')
                db.execute('INSERT OR REPLACE INTO watches VALUES (?,?,?,?)', (str(discord_id), kind, str(external_id), title))

    def watches(self, discord_id):
        with self.connect() as db:
            return [dict(r) for r in db.execute('SELECT * FROM watches WHERE discord_id=? ORDER BY title', (str(discord_id),))]

    def enqueue_action(self, discord_id, action, kind, external_id, title, payload, execute_after=None):
        from .security import UserError
        try:
            with self.connect() as db:
                cursor = db.execute('INSERT INTO actions(discord_id,action,kind,external_id,title,payload,created_at,execute_after) VALUES (?,?,?,?,?,?,?,?)',
                    (str(discord_id), action, kind, str(external_id), title, json.dumps(payload), time.time(), execute_after))
                return cursor.lastrowid
        except sqlite3.IntegrityError:
            raise UserError('This item already has a pending action. No duplicate was submitted.') from None

    def actions(self, action, pending=True):
        with self.connect() as db:
            rows = db.execute('SELECT * FROM actions WHERE action=?' + (" AND status='pending'" if pending else '') + ' ORDER BY id', (action,))
            return [dict(r) for r in rows]

    def meta(self):
        with self.connect() as db:
            return {r['key']: r['value'] for r in db.execute('SELECT * FROM hub_meta')}
