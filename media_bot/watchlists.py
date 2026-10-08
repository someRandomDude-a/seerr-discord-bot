"""Verified, user-scoped Seerr membership reconciliation. No media request/deletion."""
import json
import time

from .security import UserError


class WatchlistSync:
    def __init__(self, store, origin):
        self.store, self.origin = store, origin.rstrip('/')

    def bind(self, account):
        with self.store.connect() as db:
            db.execute('''INSERT OR IGNORE INTO watchlist_sync(discord_id,seerr_id,origin)
                VALUES (?,?,?)''', (account['discord_id'], account['seerr_id'], self.origin))
            state = db.execute('SELECT * FROM watchlist_sync WHERE discord_id=?', (account['discord_id'],)).fetchone()
            if state['seerr_id'] != account['seerr_id'] or state['origin'] != self.origin:
                raise UserError('Watchlist sync is bound to a different Seerr destination. Ask an operator to resolve the mapping; no entries were sent.')

    @staticmethod
    def read(api, seerr_id):
        """Accept only a complete, internally consistent native Seerr watchlist."""
        items, total, pages = {}, None, None
        for page in range(1, 52):
            data = api._request('GET', '/discover/watchlist', params={'page': page})
            if not isinstance(data, dict) or not isinstance(data.get('results'), list):
                raise UserError('Seerr watchlist returned an invalid response.')
            count, batches = data.get('totalResults'), data.get('totalPages')
            if (type(count) is not int or not 0 <= count <= 1000 or type(batches) is not int or
                not 0 <= batches <= 50 or batches != (count + 19) // 20 and not (count == 0 and batches == 1) or
                type(data.get('page')) is not int or data['page'] != page):
                raise UserError('Seerr watchlist pagination could not be verified.')
            if total is not None and (count, batches) != (total, pages):
                raise UserError('Seerr watchlist changed during sync. Try again.')
            total, pages = count, batches
            rows = data['results']
            expected = min(20, max(0, total - (page - 1) * 20))
            if len(rows) != expected:
                raise UserError('Seerr watchlist response was incomplete. No removals were inferred.')
            for row in rows:
                if not isinstance(row, dict):
                    raise UserError('Invalid Seerr watchlist item.')
                kind, eid, title = row.get('mediaType'), row.get('tmdbId'), row.get('title')
                if kind not in ('movie', 'tv') or type(eid) is not int or eid <= 0 or not isinstance(title, str):
                    raise UserError('Invalid Seerr watchlist identity.')
                owner = row.get('requestedBy')
                if owner is not None and (not isinstance(owner, dict) or owner.get('id') != seerr_id):
                    raise UserError('Seerr returned a different user\'s watchlist. Sync blocked.')
                key = kind, str(eid)
                if key in items:
                    raise UserError('Seerr watchlist pagination contained duplicate items.')
                items[key] = title or 'Untitled'
            if page >= max(1, pages):
                if len(items) != total:
                    raise UserError('Seerr watchlist response was incomplete.')
                return items
        raise UserError('Seerr watchlist is too large to verify safely.')

    def stable_read(self, api, seerr_id):
        first = self.read(api, seerr_id)
        second = self.read(api, seerr_id)
        if first.keys() != second.keys():
            raise UserError('Seerr watchlist changed during sync. Try again.')
        return second

    def sync(self, account, api, before_write):
        self.bind(account)
        uid = account['discord_id']
        with self.store.connect() as db:
            db.execute("UPDATE watchlist_sync SET last_attempt=?,error='Sync in progress' WHERE discord_id=?", (time.time(), uid))
        try:
            return self._sync(account, api, before_write)
        except Exception:
            with self.store.connect() as db:
                db.execute("UPDATE watchlist_sync SET error='Sync pending. Verify connectivity, service compatibility and watchlist limit.' WHERE discord_id=?", (uid,))
            raise

    def _sync(self, account, api, before_write):
        uid, remote = account['discord_id'], self.stable_read(api, account['seerr_id'])
        with self.store.connect() as db:
            # Serialize the local reconciliation with any concurrently saved intent.
            db.execute('BEGIN IMMEDIATE')
            state = db.execute('SELECT * FROM watchlist_sync WHERE discord_id=?', (uid,)).fetchone()
            local = {(r['kind'], r['external_id']): r['title'] for r in db.execute(
                "SELECT * FROM watches WHERE discord_id=? AND kind IN ('movie','tv')", (uid,))}
            pending = {(r['kind'], r['external_id']): dict(r) for r in db.execute(
                'SELECT * FROM watchlist_intents WHERE discord_id=?', (uid,))}
            baseline = {tuple(key) for key in json.loads(state['baseline'])}
            desired = local.keys() | remote.keys()
            if state['initialized']:
                # A disappearance from either converged side is a removal, not an
                # invitation to re-add it. Explicit pending hub intent wins ties.
                desired -= (baseline - local.keys()) | (baseline - remote.keys())
            for key, intent in pending.items():
                if intent['desired']:
                    desired.add(key)
                else:
                    desired.discard(key)
            other = db.execute("SELECT COUNT(*) FROM watches WHERE discord_id=? AND kind NOT IN ('movie','tv')", (uid,)).fetchone()[0]
            if len(desired) + other > 200:
                raise UserError('Combined watchlist exceeds 200 items. Remove entries in the hub or Seerr before syncing; neither list was replaced.')
            titles = {key: remote.get(key) or local.get(key) or pending[key]['title'] for key in desired}
            # Journal every outbound difference before any remote write or baseline
            # advancement. A crash/timeout cannot lose a removal or resurrect it.
            for key in desired ^ remote.keys():
                if key not in pending:
                    db.execute('INSERT INTO watchlist_intents(discord_id,kind,external_id,title,desired) VALUES (?,?,?,?,?)',
                        (uid, *key, titles.get(key) or remote[key], int(key in desired)))
            db.execute("DELETE FROM watches WHERE discord_id=? AND kind IN ('movie','tv')", (uid,))
            db.executemany('INSERT INTO watches VALUES (?,?,?,?)', [(uid, *key, titles[key]) for key in sorted(desired)])
            db.execute('UPDATE watchlist_sync SET initialized=1,baseline=? WHERE discord_id=?',
                (json.dumps(sorted(desired)), uid))
            intents = [dict(r) for r in db.execute('SELECT * FROM watchlist_intents WHERE discord_id=? ORDER BY kind,external_id', (uid,))]
        attempted = False
        for intent in intents:
            key = intent['kind'], intent['external_id']
            if (key in remote) == bool(intent['desired']):
                continue
            with self.store.connect() as db:
                current = db.execute('SELECT revision,desired FROM watchlist_intents WHERE discord_id=? AND kind=? AND external_id=?', (uid, *key)).fetchone()
            if not current or (current['revision'], current['desired']) != (intent['revision'], intent['desired']):
                continue  # A newer local choice must not be overwritten by an old plan.
            before_write()  # Live Jellyfin verification even on background writes.
            attempted = True
            try:
                if intent['desired']:
                    api._request('POST', '/watchlist', json={'mediaType': key[0], 'tmdbId': int(key[1]), 'title': intent['title']})
                else:
                    api._request('DELETE', '/watchlist/' + key[1], params={'mediaType': key[0]})
            except Exception:
                # A timeout/duplicate/not-found is ambiguous. Resolve membership
                # through a new complete read; never blindly replay a failed write.
                break
        confirmed = self.stable_read(api, account['seerr_id']) if attempted else remote
        with self.store.connect() as db:
            for intent in intents:
                key = intent['kind'], intent['external_id']
                if (key in confirmed) == bool(intent['desired']):
                    db.execute('''DELETE FROM watchlist_intents WHERE discord_id=? AND kind=? AND external_id=?
                        AND revision=? AND desired=?''', (uid, *key, intent['revision'], intent['desired']))
            remaining = db.execute('SELECT COUNT(*) FROM watchlist_intents WHERE discord_id=?', (uid,)).fetchone()[0]
            if not remaining and confirmed.keys() == desired:
                db.execute("UPDATE watchlist_sync SET last_success=?,error='' WHERE discord_id=?", (time.time(), uid))
        if remaining or confirmed.keys() != desired:
            raise UserError('Watchlist sync is pending. Your saved changes will be checked again automatically; no media was requested or deleted.')
        return 'Movie/series watchlists synced both ways. Music and books stay hub-only.'
