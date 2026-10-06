import asyncio
import json
import logging
import time
from contextlib import contextmanager
from urllib.parse import quote

from seerr import SeerrAPI, SyncManager
from .arr import ArrClient, arr_item
from .security import UserError
from .store import Store

log = logging.getLogger(__name__)
REQUEST_STATUS = {1: 'Pending approval', 2: 'Approved', 3: 'Declined', 4: 'Failed', 5: 'Completed'}
MEDIA_STATUS = {1: 'Unknown', 2: 'Requested', 3: 'Processing', 4: 'Partially available', 5: 'Available', 6: 'Files deleted'}


class MediaService:
    def __init__(self, config):
        self.config = config
        self.api = SeerrAPI(config.seerr_url, config.seerr_key, config.timeout)
        self.sync = SyncManager(self.api, config.database)
        self.store = Store(config.database)
        self.arr = {k: ArrClient(v, config.timeout) for k, v in config.arr.items()}
        # All refresh/read/mutate operations share this lock. No overlapping cache writers.
        self.lock = asyncio.Lock()

    @contextmanager
    def as_user(self, account):
        api = SeerrAPI(self.config.seerr_url, self.config.seerr_key, self.config.timeout, account['seerr_id'])
        try:
            user = api.get_current_user()
            settings = api.get_notification_settings(account['seerr_id'])
            if user['id'] != account['seerr_id'] or account['discord_id'] not in (settings.get('discordIds') or []):
                raise UserError('Your account link was revoked in Seerr. Run /link again.')
            yield api, user
        finally:
            api.close()

    def account(self, discord_id):
        account = self.store.account(discord_id)
        if not account:
            raise UserError('Link your Jellyfin account first with /link.')
        return account

    def _refresh(self):
        with self.store.connect() as db:
            db.execute("INSERT OR REPLACE INTO hub_meta VALUES ('last_attempt', ?)", (str(time.time()),))
            db.execute("INSERT OR REPLACE INTO hub_meta VALUES ('error', 'Live refresh in progress')")
        try:
            self.sync.sync()
            snapshots = {name: api.snapshot() for name, api in self.arr.items()}
            snapshots['seerr'] = self._seerr_catalog()
            watched = self._watched_states()
            with self.store.connect() as db:
                db.execute('DELETE FROM snapshots WHERE source NOT IN (' + ','.join('?' for _ in snapshots) + ')', list(snapshots))
                for source, payload in snapshots.items():
                    db.execute('INSERT OR REPLACE INTO snapshots VALUES (?,?)', (source, json.dumps(payload)))
                db.execute("INSERT OR REPLACE INTO hub_meta VALUES ('last_success', ?)", (str(time.time()),))
                db.execute("INSERT OR REPLACE INTO hub_meta VALUES ('error', '')")
                self._observe(db, snapshots, watched)
        except Exception:
            with self.store.connect() as db:
                db.execute("INSERT OR REPLACE INTO hub_meta VALUES ('error', 'Refresh failed. Operations blocked until services recover.')")
            raise UserError('Live refresh failed. No cached data was used and no action was performed. Check /status and service connectivity.') from None

    def _seerr_catalog(self):
        items, skip, ids, tv_identities, expected_count = [], 0, set(), {}, None
        while True:
            data = self.api._request('GET', '/media', params={'take': 50, 'skip': skip, 'sort': 'modified'})
            batch = data['results']
            count = (data.get('pageInfo') or {}).get('results')
            if count is not None:
                if expected_count is not None and expected_count != count:
                    raise RuntimeError('Media count changed during refresh')
                expected_count = count
            for media in batch:
                if media['id'] in ids:
                    raise RuntimeError('Media pagination changed during refresh')
                ids.add(media['id'])
                if media.get('mediaType') == 'tv' and media.get('tvdbId'):
                    tv_identities[str(media['tvdbId'])] = str(media['tmdbId'])
                if media.get('status') not in (4, 5) and media.get('status4k') not in (4, 5):
                    continue
                kind, tmdb = media['mediaType'], media['tmdbId']
                with self.store.connect() as db:
                    cached = db.execute('SELECT title, poster_path FROM media_details WHERE media_id=?', (media['id'],)).fetchone()
                if cached:
                    title, poster = cached['title'], cached['poster_path']
                else:
                    details = self.api.get_movie_details(tmdb) if kind == 'movie' else self.api.get_tv_details(tmdb)
                    title, poster = details.get('title') or details.get('name') or 'Untitled', details.get('posterPath')
                    with self.store.connect() as db:
                        db.execute('INSERT OR REPLACE INTO media_details(media_id,type,tmdb_id,title,poster_path) VALUES (?,?,?,?,?)',
                                   (media['id'], kind, tmdb, title, poster))
                regular = media.get('status') in (4, 5)
                items.append({'kind': kind, 'external_id': str(tmdb), 'title': title, 'poster_path': poster,
                    'available': True, 'source': 'seerr', 'tvdb_id': str(media.get('tvdbId') or ''),
                    'jellyfin_id': media.get('jellyfinMediaId') if regular else media.get('jellyfinMediaId4k'),
                    'subtitle': MEDIA_STATUS.get(media.get('status' if regular else 'status4k'), 'Unknown') + ('' if regular else ' · 4K')})
            if len(batch) < 50:
                if expected_count is not None and len(ids) != expected_count:
                    raise RuntimeError('Incomplete media pagination')
                return {'items': items, 'disks': [], 'tv_identities': tv_identities}
            skip += 50

    def _watched_states(self):
        # Watchlists must detect availability even when nobody has requested the media.
        with self.store.connect() as db:
            watches = list(db.execute('SELECT DISTINCT w.kind,w.external_id,w.title FROM watches w JOIN accounts a ON a.discord_id=w.discord_id WHERE a.opted_in=1'))
        states = []
        for row in watches:
            if row['kind'] not in ('movie', 'tv'):
                continue
            details = self.api.get_movie_details(int(row['external_id'])) if row['kind'] == 'movie' else self.api.get_tv_details(int(row['external_id']))
            info = details.get('mediaInfo') or {}
            state = MEDIA_STATUS.get(info.get('status'), 'Not requested')
            if info.get('status4k') in (4, 5):
                state += ' · Available in 4K'
            states.append((row['kind'], row['external_id'], row['title'], state))
        return states

    async def refresh(self):
        async with self.lock:
            await self.offload(self._refresh)

    @staticmethod
    async def offload(function, *args):
        # Cancellation cannot release the service lock while a worker still writes.
        task = asyncio.create_task(asyncio.to_thread(function, *args))
        try:
            return await asyncio.shield(task)
        except asyncio.CancelledError:
            try:
                await task
            except Exception:
                pass
            raise

    async def run(self, discord_id, operation, *args):
        async with self.lock:
            account = self.account(discord_id)
            await self.offload(self._refresh)
            return await self.offload(self._authorized, account, operation, args)

    def _authorized(self, account, operation, args):
        with self.as_user(account) as (api, user):
            try:
                return getattr(self, '_op_' + operation)(account, api, user, *args)
            except UserError:
                raise
            except Exception:
                raise UserError('The service rejected the operation or could not be reached. Refresh and try again; contact an administrator if it persists.') from None

    def link_authenticated(self, discord_id, client):
        """Bind Discord only after native Quick Connect authenticated a cookie session."""
        user = client.get_current_user()
        uid = int(user['id'])
        with self.store.connect() as db:
            conflict = db.execute('SELECT discord_id FROM accounts WHERE seerr_id=? AND discord_id!=?', (uid, str(discord_id))).fetchone()
        if conflict:
            raise UserError('This Seerr account is already linked to another Discord account.')
        previous = self.store.account(discord_id)
        if previous and previous['seerr_id'] != uid:
            raise UserError('This Discord account is already linked to a different Seerr account. Resolve the existing mapping before relinking.')
        settings = client.get_notification_settings(uid)
        ids = settings.get('discordIds') or []
        if any(str(value) != str(discord_id) for value in ids):
            raise UserError('This Seerr account has a different Discord ID. Resolve it in Seerr before linking.')
        # Seerr's POST replaces fields, so preserve unrelated notification preferences.
        writable = ('pgpKey', 'pushbulletAccessToken', 'pushoverApplicationToken', 'pushoverUserKey',
                    'pushoverSound', 'telegramChatId', 'telegramMessageThreadId', 'telegramSendSilently', 'notificationTypes')
        updated = {key: settings[key] for key in writable if key in settings}
        updated['discordIds'] = [str(discord_id)]
        client.update_notification_settings(uid, updated)
        verified = client.get_notification_settings(uid)
        if str(discord_id) not in (verified.get('discordIds') or []):
            raise UserError('Seerr did not save the Discord ID. Upgrade Seerr to a version supporting discordIds.')
        name = user.get('displayName') or user.get('jellyfinUsername') or user.get('username') or f'Seerr user {uid}'
        self.store.link(discord_id, uid, name)
        return name

    def _op_dashboard(self, account, api, user):
        return {'account': account, 'requests': len(self._op_requests(account, api, user, False)),
                'library': len(self._op_library(account, api, user, 'all')), 'meta': self.store.meta()}

    def _op_requests(self, account, api, user, all_requests=False):
        permissions = int(user.get('permissions', 0))
        if all_requests and not permissions & (2 | 16 | 16384):
            raise UserError('Browsing all requests requires Seerr REQUEST_VIEW or MANAGE_REQUESTS permission.')
        with self.store.connect() as db:
            query = 'SELECT * FROM requests' + ('' if all_requests else ' WHERE requested_by_id=?') + ' ORDER BY updated_at DESC'
            rows = db.execute(query, () if all_requests else (account['seerr_id'],)).fetchall()
            items = []
            for row in rows:
                item = dict(row)
                item.update(kind=row['type'], external_id=str(row['tmdb_id']), jellyfin_id=row['jellyfin_id'],
                            available=row['media_status'] in (4, 5),
                            deletable=row['requested_by_id'] == account['seerr_id'],
                            subtitle=f"{REQUEST_STATUS.get(row['status'], 'Unknown')} · {MEDIA_STATUS.get(row['media_status'], 'Unknown')}")
                items.append(item)
            return items

    def _op_storage(self, account, api, user):
        with self.store.connect() as db:
            return {r['source']: json.loads(r['payload'])['disks'] for r in db.execute("SELECT * FROM snapshots WHERE source!='seerr'")}

    def _op_library(self, account, api, user, kind):
        with self.store.connect() as db:
            catalog = db.execute("SELECT payload FROM snapshots WHERE source='seerr'").fetchone()
        items = json.loads(catalog['payload'])['items'] if catalog else []
        seen = {(i['kind'], i['external_id'], 'tmdb') for i in items}
        seen_tvdb = {i.get('tvdb_id') for i in items if i['kind'] == 'tv'}
        with self.store.connect() as db:
            for row in db.execute("SELECT * FROM snapshots WHERE source!='seerr'"):
                for raw in json.loads(row['payload'])['items']:
                    item = arr_item(row['source'], raw)
                    # Sonarr identifiers are TVDB rather than TMDB; keep its separate source.
                    if row['source'] == 'sonarr' and item['external_id'] in seen_tvdb:
                        continue
                    namespace = 'tvdb' if row['source'] == 'sonarr' else 'tmdb' if row['source'] == 'radarr' else row['source']
                    identity = (item['kind'], item['external_id'], namespace)
                    if item['available'] and identity not in seen:
                        items.append(item)
                        seen.add(identity)
        return sorted([i for i in items if kind == 'all' or i['kind'] == kind], key=lambda i: i['title'].lower())

    def _op_search(self, account, api, user, kind, query, page=1):
        if not 1 <= len(query.strip()) <= 100:
            raise UserError('Search must be between 1 and 100 characters.')
        if kind in ('music', 'book'):
            source = 'lidarr' if kind == 'music' else 'readarr'
            if source not in self.arr:
                raise UserError(f'{source.title()} is not configured.')
            return [arr_item(source, row) for row in self.arr[source].search(query)]
        data = api.search(query, page=page)
        return [{'kind': row['mediaType'], 'external_id': str(row['id']),
                 'title': row.get('title') or row.get('name') or 'Untitled',
                 'overview': row.get('overview') or '', 'poster_path': row.get('posterPath'),
                 'available': (row.get('mediaInfo') or {}).get('status') in (4, 5),
                 'requestable': (row.get('mediaInfo') or {}).get('status') not in (2, 3, 4, 5),
                 'jellyfin_id': (row.get('mediaInfo') or {}).get('jellyfinMediaId'),
                 'subtitle': (row.get('releaseDate') or row.get('firstAirDate') or '')[:4]}
                for row in data['results'] if row.get('mediaType') == kind]

    def _op_request(self, account, api, user, item):
        kind, eid = item['kind'], item['external_id']
        if kind in ('movie', 'tv'):
            details = api.get_movie_details(int(eid)) if kind == 'movie' else api.get_tv_details(int(eid))
            state = (details.get('mediaInfo') or {}).get('status')
            if state in (2, 3, 4, 5):
                raise UserError('This item is already requested or available. Open it in Seerr for individual season requests.')
            result = api.create_request(kind, int(eid))  # X-API-User enforces real permissions/quotas.
            self._invalidate('Request submitted; refreshing state.')
            try:
                self._refresh()
                return f"Request #{result['id']} submitted."
            except UserError:
                return f"Request #{result['id']} was submitted, but follow-up sync failed. Do not resubmit; check /status."
        if not (int(user.get('permissions', 0)) & (2 | 32)):
            raise UserError('Seerr REQUEST permission is required to submit music/book requests.')
        source = 'lidarr' if kind == 'music' else 'readarr'
        matches = self._op_search(account, api, user, kind, item['title'])
        fresh = next((i for i in matches if i['external_id'] == eid), None)
        if not fresh or fresh['available']:
            raise UserError('This lookup is no longer requestable. Search again.')
        with self.store.connect() as db:
            count = db.execute("SELECT COUNT(*) FROM actions WHERE discord_id=? AND action='add' AND created_at>? AND status!='failed'",
                               (account['discord_id'], time.time() - 86400)).fetchone()[0]
        if count >= self.config.arr_daily_limit:
            raise UserError('Your daily music/book request limit has been reached. Try again later.')
        with self.store.connect() as db:
            snapshot = db.execute('SELECT payload FROM snapshots WHERE source=?', (source,)).fetchone()
        if any(arr_item(source, row)['external_id'] == eid for row in json.loads(snapshot['payload'])['items']):
            raise UserError('This item is already in the service, even if its files are not downloaded yet.')
        self.arr[source].validate_config()
        action_id = self.store.enqueue_action(account['discord_id'], 'add', kind, eid, fresh['title'], {'source': source})
        with self.store.connect() as db:
            db.execute("UPDATE actions SET status='processing' WHERE id=?", (action_id,))
        self._invalidate('Library addition in progress.')
        try:
            self.arr[source].add(fresh['raw'])
        except Exception:
            with self.store.connect() as db:
                db.execute("UPDATE actions SET status='uncertain',error='Verify upstream before retrying' WHERE id=?", (action_id,))
            raise UserError('The service did not confirm the request. Do not resubmit until its library is checked; the bot will not automatically retry an ambiguous write.') from None
        with self.store.connect() as db:
            db.execute("UPDATE actions SET status='completed' WHERE id=?", (action_id,))
        try:
            self.store.watch(account['discord_id'], kind, eid, fresh['title'])
        except UserError:
            pass  # A full watchlist must not misreport a successful upstream request.
        try:
            self._refresh()
            return 'Your library request was submitted.'
        except UserError:
            return 'Your library request was submitted, but follow-up sync failed. Do not resubmit; check /status.'

    def _op_watch(self, account, api, user, item, remove=False):
        if item['kind'] == 'tv' and item.get('source') == 'sonarr':
            raise UserError('Search this series through Seerr to follow its TMDB identity.')
        if not remove:
            kind, eid = item['kind'], item['external_id']
            if kind in ('movie', 'tv'):
                details = api.get_movie_details(int(eid)) if kind == 'movie' else api.get_tv_details(int(eid))
                item = dict(item, title=details.get('title') or details.get('name') or 'Untitled')
            else:
                matches = self._op_search(account, api, user, kind, item['title'])
                item = next((i for i in matches if i['external_id'] == eid), None)
                if not item:
                    raise UserError('This item could not be verified. Search again.')
        self.store.watch(account['discord_id'], item['kind'], item['external_id'], item['title'], remove)
        return 'Removed from watchlist.' if remove else 'Added to your watchlist. Enable notifications to receive DMs.'

    def _op_open(self, account, api, user, item):
        if item['kind'] not in ('movie', 'tv') or item.get('source') == 'sonarr':
            raise UserError('This item does not have a Seerr/Jellyfin link.')
        details = api.get_movie_details(int(item['external_id'])) if item['kind'] == 'movie' else api.get_tv_details(int(item['external_id']))
        info = details.get('mediaInfo') or {}
        regular = info.get('status') in (4, 5)
        if not regular and info.get('status4k') not in (4, 5):
            raise UserError('This item is no longer available.')
        url = self.jellyfin_link(account, {'jellyfin_id': info.get('jellyfinMediaId' if regular else 'jellyfinMediaId4k')})
        if not url:
            raise UserError('No Jellyfin ID or configured device link is available for this item.')
        return url

    def _op_watches(self, account, api, user):
        return [dict(w, available=False, subtitle='Followed') for w in self.store.watches(account['discord_id'])]

    async def watches(self, discord_id):
        account = self.account(discord_id)
        return [dict(w, available=False, subtitle='Followed') for w in self.store.watches(account['discord_id'])]

    async def unwatch(self, discord_id, item):
        account = self.account(discord_id)
        self.store.watch(account['discord_id'], item['kind'], item['external_id'], item.get('title', ''), remove=True)
        return 'Unfollowed.'

    def _op_import_watchlist(self, account, api, user):
        page, total = 1, 0
        while True:
            data = api._request('GET', '/discover/watchlist', params={'page': page})
            for row in data['results']:
                kind = row.get('type') or row.get('mediaType')
                if kind in ('movie', 'tv') and row.get('tmdbId'):
                    self.store.watch(account['discord_id'], kind, row['tmdbId'], row.get('title') or 'Untitled')
                    total += 1
            if page >= data.get('totalPages', 1):
                return f'Imported {total} Seerr watchlist items. Repeat after changing your Seerr watchlist.'
            page += 1

    def _op_preferences(self, account, api, user, opted_in=None, device=None):
        if device is not None and device not in self.config.devices:
            raise UserError('This device is not configured.')
        self.store.preference(account['discord_id'], opted_in=opted_in, device=device)
        return 'Preferences saved.'

    def _op_delete(self, account, api, user, request_id):
        req = api.get_request(int(request_id))  # Recheck ownership and availability at submission time.
        if (req.get('requestedBy') or {}).get('id') != account['seerr_id']:
            raise UserError('Only the original requester can schedule file deletion.')
        media = req['media']
        is4k = bool(req.get('is4k'))
        if media.get('status4k' if is4k else 'status') not in (4, 5):
            raise UserError('There are no available files to delete for this request.')
        with self.store.connect() as db:
            title = db.execute('SELECT title FROM requests WHERE id=?', (request_id,)).fetchone()['title']
        deadline = time.time() + 86400
        action_id = self.store.enqueue_action(account['discord_id'], 'delete', req['type'],
             f"{media['id']}:{int(is4k)}", title, {'request_id': request_id, 'media_id': media['id'],
              'tmdb_id': media['tmdbId'], 'is4k': is4k}, execute_after=deadline)
        return f'File deletion #{action_id} is scheduled for <t:{int(deadline)}:F> (24 hours). Use /deletions to undo it before execution. Seerr request history will be kept.'

    async def deletions(self, discord_id):
        account = self.account(discord_id)
        with self.store.connect() as db:
            rows = db.execute("SELECT * FROM actions WHERE discord_id=? AND action='delete' ORDER BY id DESC", (account['discord_id'],)).fetchall()
            return [dict(r, available=False, subtitle=f"#{r['id']} · {r['status']} · {'4K' if json.loads(r['payload'])['is4k'] else 'Standard'}") for r in rows]

    async def cancel_deletion(self, discord_id, action_id):
        # Local atomic undo remains available even if upstream services are offline.
        account = self.account(discord_id)
        # No network lock: an undo can win while a live refresh is still underway.
        with self.store.connect() as db:
            changed = db.execute("UPDATE actions SET status='cancelled' WHERE id=? AND discord_id=? AND action='delete' AND status='pending'",
                                 (action_id, account['discord_id'])).rowcount
            if not changed:
                raise UserError('Deletion cannot be undone: it was already cancelled or execution has begun.')
        return 'Deletion undone. No files were removed.'

    def mute_notifications(self, discord_id):
        self.account(discord_id)
        self.store.preference(discord_id, opted_in=False)

    async def process_due_deletions(self):
        with self.store.connect() as db:
            due = [r['id'] for r in db.execute("SELECT id FROM actions WHERE action='delete' AND status='pending' AND execute_after<=? ORDER BY execute_after", (time.time(),))]
        for action_id in due:
            async with self.lock:
                try:
                    await self.offload(self._refresh)  # Fail closed: pending remains retryable if freshness fails.
                    await self.offload(self._execute_deletion, action_id)
                except UserError:
                    log.warning('Scheduled deletion delayed or not confirmed (#%s)', action_id)

    def _execute_deletion(self, action_id):
        with self.store.connect() as db:
            row = db.execute("SELECT * FROM actions WHERE id=? AND status='pending' AND execute_after<=?", (action_id, time.time())).fetchone()
        if not row:
            return
        payload = json.loads(row['payload'])
        write_started = False
        try:
            requester = self.account(row['discord_id'])
            with self.as_user(requester) as (requester_api, requester_user):
                req = requester_api.get_request(payload['request_id'])
                if (req['media']['id'] != payload['media_id'] or bool(req.get('is4k')) != payload['is4k'] or
                    req['type'] != row['kind'] or req['media'].get('tmdbId') != payload.get('tmdb_id')):
                    raise UserError('The target changed. Submit a new deletion request.')
                if (req.get('requestedBy') or {}).get('id') != requester['seerr_id']:
                    raise UserError('The requester no longer owns this request.')
                if req['media'].get('status4k' if payload['is4k'] else 'status') not in (4, 5):
                    raise UserError('The files are no longer available.')
                with self.store.connect() as db:
                    claimed = db.execute("UPDATE actions SET status='processing' WHERE id=? AND status='pending'", (action_id,)).rowcount
                if not claimed:
                    return
                write_started = True
                # This is the ONLY remote deletion endpoint used. Never delete requests/media records.
                self._invalidate('File deletion in progress.')
                self.api.delete_media_files(payload['media_id'], payload['is4k'])
        except UserError as exc:
            with self.store.connect() as db:
                db.execute("UPDATE actions SET status='cancelled',error=? WHERE id=?", (str(exc), action_id))
            return
        except Exception:
            if not write_started:
                raise UserError('Live ownership check failed. Deletion remains pending and undoable.') from None
            # HTTP timeouts are ambiguous: never auto-retry or allow a duplicate action.
            with self.store.connect() as db:
                db.execute("UPDATE actions SET status='uncertain',error='Verify upstream result before resolving' WHERE id=?", (action_id,))
            raise UserError('File deletion not confirmed; marked uncertain to prevent duplicate execution.') from None
        with self.store.connect() as db:
            db.execute("UPDATE actions SET status='completed' WHERE id=?", (action_id,))
        try:
            self._refresh()
            return
        except UserError:
            log.warning('Deletion completed but follow-up refresh failed (#%s)', action_id)

    def _invalidate(self, reason):
        with self.store.connect() as db:
            db.execute("INSERT OR REPLACE INTO hub_meta VALUES ('error', ?)", (reason,))

    def jellyfin_link(self, account, item):
        device = account.get('device') or next(iter(self.config.devices), None)
        base = self.config.devices.get(device)
        item_id = item.get('jellyfin_id')
        if not base or not item_id:
            return None
        return f"{base.rstrip('/')}/web/index.html#!/details?id={quote(str(item_id), safe='')}"

    def _observe(self, db, snapshots, watched):
        states = {}
        for row in db.execute('SELECT * FROM requests'):
            key = (row['type'], str(row['tmdb_id']))
            # One notification state per media identity, not per duplicate user's request.
            entry = states.setdefault(key, {'title': row['title'], 'states': set(), 'users': set()})
            entry['states'].add(f"{REQUEST_STATUS.get(row['status'], 'Unknown')} / {MEDIA_STATUS.get(row['media_status'], 'Unknown')}" + (' (4K)' if row['is4k'] else ''))
            entry['users'].add(row['requested_by_id'])
        for source in ('lidarr', 'readarr'):
            for raw in snapshots.get(source, {}).get('items', []):
                item = arr_item(source, raw)
                states[(item['kind'], item['external_id'])] = {'title': item['title'], 'states': {'Available' if item['available'] else 'Monitored'}, 'users': set()}
        for kind, eid, title, state in watched:
            if (kind, eid) not in states:
                states[(kind, eid)] = {'title': title, 'states': {state}, 'users': set()}
        # Connect notifications are verified against each service's live library,
        # even if Seerr has not yet run its own availability scan.
        tv_ids = snapshots.get('seerr', {}).get('tv_identities', {})
        for source in ('radarr', 'sonarr'):
            if source not in snapshots:
                continue
            service_items = {}
            for raw in snapshots[source]['items']:
                item = arr_item(source, raw)
                eid = tv_ids.get(item['external_id']) if source == 'sonarr' else item['external_id']
                if eid:
                    service_items[(item['kind'], eid)] = item
            kind = 'movie' if source == 'radarr' else 'tv'
            for key, entry in states.items():
                if key[0] != kind:
                    continue
                item = service_items.get(key)
                entry['states'].add(f"{source.title()}: " + ('Available' if item and item['available'] else 'No files'))
        for row in db.execute('SELECT DISTINCT kind,external_id,title FROM watches WHERE kind IN (\'music\',\'book\')'):
            key = (row['kind'], row['external_id'])
            source = 'lidarr' if row['kind'] == 'music' else 'readarr'
            if source in snapshots and key not in states:
                states[key] = {'title': row['title'], 'states': {'Not in library'}, 'users': set()}
        baseline = db.execute("SELECT value FROM hub_meta WHERE key='notification_baseline'").fetchone()
        for (kind, eid), entry in states.items():
            state = ' · '.join(sorted(entry['states']))
            previous = db.execute('SELECT * FROM observed WHERE kind=? AND external_id=?', (kind, eid)).fetchone()
            if previous and previous['state'] == state:
                continue
            revision = previous['revision'] + 1 if previous else 1
            db.execute('INSERT OR REPLACE INTO observed VALUES (?,?,?,?)', (kind, eid, state, revision))
            if not baseline:
                continue  # No historical notification flood on first startup.
            recipients = db.execute('''SELECT DISTINCT a.discord_id FROM accounts a
                LEFT JOIN watches w ON w.discord_id=a.discord_id AND w.kind=? AND w.external_id=?
                WHERE a.opted_in=1 AND (w.external_id IS NOT NULL OR a.seerr_id IN (
                    SELECT requested_by_id FROM requests WHERE type=? AND tmdb_id=?))''', (kind, eid, kind, eid)).fetchall()
            for recipient in recipients:
                db.execute('INSERT OR IGNORE INTO outbox(discord_id,event_key,title,body) VALUES (?,?,?,?)',
                           (recipient['discord_id'], f'{kind}:{eid}:{revision}', entry['title'], state))
        db.execute("INSERT OR REPLACE INTO hub_meta VALUES ('notification_baseline', '1')")
