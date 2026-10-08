import asyncio
import json
import logging
import time
from contextlib import contextmanager
from urllib.parse import quote

from seerr import SeerrAPI, SyncManager
from .arr import ArrClient, arr_item, external_id
from .security import UserError, VerificationRequired, VerificationUnavailable
from .jellyfin import JellyfinClient, discover_jellyfin, identity
from .store import Store
from .discovery import discover_arr
from .watchlists import WatchlistSync

log = logging.getLogger(__name__)
REQUEST_STATUS = {1: 'Pending approval', 2: 'Approved', 3: 'Declined', 4: 'Failed', 5: 'Completed'}
MEDIA_STATUS = {1: 'Unknown', 2: 'Requested', 3: 'Processing', 4: 'Partially available', 5: 'Available', 6: 'Files deleted'}
# These operations fetch their own live dependencies; refreshing every library,
# disk and notification watch beforehand adds latency without making them fresher.
LIVE_OPERATIONS = frozenset(('search', 'discover', 'details', 'open', 'preferences', 'account', 'seerr_link', 'import_watchlist', 'sync_watchlist', 'watch'))
QUICK_READ_OPERATIONS = LIVE_OPERATIONS - {'preferences', 'import_watchlist', 'sync_watchlist', 'watch'}
WATCHLIST_OPERATIONS = frozenset(('watches', 'watch', 'import_watchlist', 'sync_watchlist'))


class MediaService:
    def __init__(self, config):
        self.config = config
        self.api = SeerrAPI(config.seerr_url, config.seerr_key, config.timeout)
        try:
            self.sync = SyncManager(self.api, config.database)
            self.store = Store(config.database)
        except Exception:
            self.api.close()
            raise
        self.arr = {k: ArrClient(v, config.timeout) for k, v in config.arr.items()}
        self.watchlists = WatchlistSync(self.store, config.seerr_url)
        self.watchlist_locks = {}
        # Snapshot refreshes/mutations share this lock; independent live reads do not write snapshots.
        self.lock = asyncio.Lock()

    @contextmanager
    def as_user(self, account):
        self.verify_identity(account['discord_id'])
        api = SeerrAPI(self.config.seerr_url, self.config.seerr_key, self.config.timeout, account['seerr_id'])
        try:
            user = api.get_current_user()
            settings = api.get_notification_settings(account['seerr_id'])
            if user['id'] != account['seerr_id'] or account['discord_id'] not in (settings.get('discordIds') or []):
                raise VerificationRequired('Your account link was revoked in Seerr. Run /link again.')
            credential = self.store.credential(account['discord_id'])
            if identity(user.get('jellyfinUserId')) != identity(credential['user_id']):
                raise VerificationRequired('Account verification changed. Run /link again.')
            yield api, user
        finally:
            api.close()

    def account(self, discord_id):
        account = self.store.account(discord_id)
        if not account:
            raise VerificationRequired('Link your Jellyfin account first with /link.')
        return account

    def jellyfin_settings(self):
        api = SeerrAPI(self.config.seerr_url, self.config.seerr_key, self.config.timeout)
        try:
            return discover_jellyfin(api)
        finally:
            api.close()

    def verify_identity(self, discord_id):
        account = self.account(discord_id)
        try:
            credential = self.store.credential(discord_id)
        except Exception:
            raise VerificationRequired('Verification needs renewal. Run /link again.') from None
        if not credential:
            raise VerificationRequired('Verify your identity with /link before accessing the hub.')
        try:
            settings = self.jellyfin_settings()
            if settings.url != credential['url'] or settings.server_id != credential['server_id']:
                raise VerificationRequired('Verification needs renewal. Run /link again.')
            client = JellyfinClient(settings.url, credential['device_id'], self.config.timeout, credential['token'])
            user = client.me()
            if (identity(user.get('Id')) != identity(credential['user_id']) or
                identity(user.get('ServerId')) != credential['server_id'] or (user.get('Policy') or {}).get('IsDisabled')):
                raise VerificationRequired('Verification expired or was revoked. Run /link again.')
        except VerificationRequired:
            self.store.revoke_credential(discord_id, credential['token'])
            raise
        except Exception:
            raise VerificationUnavailable('Identity verification is unavailable. No private data can be shown. Try again later.') from None
        return account

    async def verified_account(self, discord_id):
        return await self.offload(self.verify_identity, discord_id)

    def devices(self):
        return {'Browser': self.jellyfin_settings().external_url, **self.config.devices}

    def profile(self, discord_id):
        try:
            self.verify_identity(discord_id)
            devices = list(self.devices())
            admin = self.is_admin(discord_id)
            # A second admin check may detect revocation; do not return the earlier
            # account/device snapshot simply because is_admin safely returned False.
            account = self.verify_identity(discord_id)
            return {'account': account, 'devices': devices, 'is_admin': admin,
                'refresh_interval': self.config.activity_refresh_interval, 'sync_interval': self.config.sync_interval}
        except VerificationRequired:
            return {'account': None, 'devices': [], 'is_admin': False, 'verification_required': True}
        except VerificationUnavailable:
            raise
        except Exception:
            raise VerificationUnavailable('Identity verification is unavailable. No private data can be shown. Try again later.') from None

    def status(self, discord_id):
        self.verify_identity(discord_id)
        return self.store.meta()

    def is_admin(self, discord_id):
        try:
            self.verify_identity(discord_id)
            if int(discord_id) in self.config.admin_ids:
                return True  # Bot-only role exception, not an identity-verification exception.
            if not self.config.seerr_admins:
                return False
            account = self.account(discord_id)
            with self.as_user(account) as (_api, user):
                return user['id'] == 1 or bool(int(user.get('permissions', 0)) & 2)
        except VerificationUnavailable:
            raise
        except Exception:
            return False

    async def verify_admin(self, discord_id):
        try:
            return await self.offload(self.is_admin, discord_id)
        except Exception:
            return False  # Profile may still expose local Undo/mute; admin actions always revalidate separately.

    def _refresh(self):
        with self.store.connect() as db:
            db.execute("INSERT OR REPLACE INTO hub_meta VALUES ('last_attempt', ?)", (str(time.time()),))
            db.execute("INSERT OR REPLACE INTO hub_meta VALUES ('error', 'Live refresh in progress')")
        try:
            self.sync.sync()
            if self.config.seerr_discovery:
                discovered = discover_arr(self.api)
                direct = {k: v for k, v in self.config.arr.items() if k not in ('radarr', 'sonarr')}
                configs = {**direct, **discovered}
                self.arr = {k: ArrClient(v, self.config.timeout) for k, v in configs.items()}
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
                    'available': True, 'source': 'seerr', 'is4k': not regular, 'tvdb_id': str(media.get('tvdbId') or ''),
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
        return await self._run(discord_id, operation, args)

    async def browse(self, discord_id, operation, *args):
        if operation not in ('dashboard', 'library', 'requests', 'search', 'discover', 'storage', 'watches', 'deletions', 'account'):
            raise UserError('Invalid browsing destination.')
        return await self._run(discord_id, operation, args, include_viewer=True)

    async def _run(self, discord_id, operation, args, include_viewer=False):
        if operation in WATCHLIST_OPERATIONS:
            account = self.account(discord_id)
            lock = self.watchlist_locks.setdefault(str(discord_id), asyncio.Lock())
            async with lock:
                return await self.offload(self._authorized, account, operation, args, include_viewer)
        if operation in QUICK_READ_OPERATIONS:
            # Read-only, independently authenticated upstream calls need neither
            # the snapshot-writer lock nor a refresh of unrelated integrations.
            account = self.account(discord_id)
            return await self.offload(self._authorized, account, operation, args, include_viewer)
        async with self.lock:
            account = self.account(discord_id)
            await self.offload(self.verify_identity, discord_id)
            if operation not in LIVE_OPERATIONS and operation not in ('watches', 'deletions'):
                await self.offload(self._refresh)
            return await self.offload(self._authorized, account, operation, args, include_viewer)

    def _authorized(self, account, operation, args, include_viewer=False):
        with self.as_user(account) as (api, user):
            try:
                result = getattr(self, '_op_' + operation)(account, api, user, *args)
                if include_viewer:
                    viewer = {'account': self.account(account['discord_id']), 'devices': list(self.devices()),
                        'is_admin': int(account['discord_id']) in self.config.admin_ids or
                            self.config.seerr_admins and (user['id'] == 1 or bool(int(user.get('permissions', 0)) & 2)),
                        'refresh_interval': self.config.activity_refresh_interval, 'sync_interval': self.config.sync_interval}
                    return {'viewer': viewer, 'result': result, 'meta': self.store.meta()}
                return result
            except UserError:
                raise
            except Exception:
                raise UserError('The service rejected the operation or could not be reached. Refresh and try again; contact an administrator if it persists.') from None

    def validate_browse(self, discord_id, all_requests=False, snapshot=True):
        """Revalidate access to an already-open gallery, not its entire upstream catalog."""
        account = self.account(discord_id)
        with self.as_user(account) as (_api, user):
            if all_requests and not int(user.get('permissions', 0)) & (2 | 16 | 16384):
                raise UserError('Browsing all requests requires Seerr REQUEST_VIEW or MANAGE_REQUESTS permission.')
        meta = self.store.meta()
        if snapshot and meta.get('error'):
            raise UserError('Live refresh failed. Refresh the gallery after services recover.')

    def _op_account(self, account, api, user):
        return None

    def _op_seerr_link(self, account, api, user):
        return self.config.seerr_url

    def link_authenticated(self, discord_id, client, credential):
        """Bind Discord only after native Quick Connect authenticated a cookie session."""
        user = client.get_current_user()
        if identity(user.get('jellyfinUserId')) != identity(credential['user_id']):
            raise VerificationRequired('The approved identity did not match. Run /link again.')
        uid = int(user['id'])
        with self.store.connect() as db:
            conflict = db.execute('SELECT discord_id FROM accounts WHERE seerr_id=? AND discord_id!=?', (uid, str(discord_id))).fetchone()
        if conflict:
            raise UserError('This Seerr account is already linked to another Discord account.')
        previous = self.store.account(discord_id)
        if previous and previous['seerr_id'] != uid:
            raise UserError('This Discord account is already linked to a different Seerr account. Resolve the existing mapping before relinking.')
        bound = self.store.bound_identity(discord_id)
        if bound and (identity(bound['user_id']) != identity(credential['user_id']) or bound['server_id'] != credential['server_id']):
            raise VerificationRequired('This Discord account is bound to a different identity.')
        with self.store.connect() as db:
            conflict = db.execute('SELECT discord_id FROM jellyfin_identities WHERE user_id=? AND server_id=? AND discord_id!=?',
                (credential['user_id'], credential['server_id'], str(discord_id))).fetchone()
        if conflict:
            raise VerificationRequired('This identity is already bound to another Discord account.')
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
        self.store.link(discord_id, uid, name, credential)
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
                    source_type = row['source'].split(':')[0]
                    # Sonarr identifiers are TVDB rather than TMDB; keep its separate source.
                    if source_type == 'sonarr' and item['external_id'] in seen_tvdb:
                        continue
                    namespace = 'tvdb' if source_type == 'sonarr' else 'tmdb' if source_type == 'radarr' else row['source']
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
        return self._seerr_results(data, kind)

    @staticmethod
    def _seerr_results(data, kind):
        return [{'kind': row['mediaType'], 'external_id': str(row['id']),
                 'title': row.get('title') or row.get('name') or 'Untitled',
                 'overview': row.get('overview') or '', 'poster_path': row.get('posterPath'),
                 'available': (row.get('mediaInfo') or {}).get('status') in (4, 5),
                 'requestable': (row.get('mediaInfo') or {}).get('status') not in (2, 3, 4, 5),
                 'jellyfin_id': (row.get('mediaInfo') or {}).get('jellyfinMediaId'),
                 'subtitle': (row.get('releaseDate') or row.get('firstAirDate') or '')[:4]}
                for row in data['results'] if row.get('mediaType') == kind]

    def _op_discover(self, account, api, user, kind='movie', page=1):
        if kind not in ('movie', 'tv') or not 1 <= page <= 500:
            raise UserError('Choose movies or series to discover popular titles.')
        data = api._request('GET', '/discover/movies' if kind == 'movie' else '/discover/tv',
            params={'page': page, 'sortBy': 'popularity.desc'})
        return self._seerr_results(data, kind)

    def _op_details(self, account, api, user, item):
        kind, source = item['kind'], (item.get('source') or '').split(':')[0]
        if kind in ('movie', 'tv') and source != 'sonarr':
            details = api.get_movie_details(int(item['external_id'])) if kind == 'movie' else api.get_tv_details(int(item['external_id']))
            info = details.get('mediaInfo') or {}
            # Request rows keep their own edition/ownership fields; action methods
            # still recheck permissions, ownership and availability immediately.
            return {**item, 'title': details.get('title') or details.get('name') or 'Untitled',
                'overview': details.get('overview') or '', 'poster_path': details.get('posterPath'),
                'available': info.get('status4k' if item.get('is4k') else 'status') in (4, 5),
                'requestable': info.get('status') not in (2, 3, 4, 5)}
        if kind in ('music', 'book'):
            matches = self._op_search(account, api, user, kind, item['title'])
            fresh = next((row for row in matches if row['external_id'] == item['external_id']), None)
        elif kind == 'tv' and source == 'sonarr':
            # Sonarr uses TVDB identities, not Seerr's TMDB IDs. Refresh only the
            # selected instance's series metadata, never its disks or other libraries.
            name = item['source']
            configs = discover_arr(self.api) if self.config.seerr_discovery else self.config.arr
            if name not in configs or configs[name].name != 'sonarr':
                raise UserError('This series source is no longer configured. Refresh the library.')
            rows = ArrClient(configs[name], self.config.timeout).request('GET', 'series')
            fresh = next((arr_item(name, row) for row in rows if external_id(name, row) == item['external_id']), None)
        else:
            fresh = None
        if not fresh:
            raise UserError('This item could not be verified live. Search again or refresh the library.')
        return {**item, **fresh}

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
        if item['kind'] == 'tv' and (item.get('source') or '').split(':')[0] == 'sonarr':
            raise UserError('Search this series through Seerr to follow its TMDB identity.')
        if item['kind'] in ('movie', 'tv'):
            self.watchlists.bind(account)
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
        if item['kind'] in ('movie', 'tv'):
            try:
                self._op_sync_watchlist(account, api, user)
            except (VerificationRequired, VerificationUnavailable):
                raise
            except Exception:
                return 'Watchlist change saved. Seerr sync is pending and will retry automatically; no media was requested or deleted.'
        return 'Removed from watchlist.' if remove else 'Added to your watchlist. Enable notifications to receive DMs.'

    def _op_open(self, account, api, user, item):
        if item['kind'] not in ('movie', 'tv'):
            raise UserError('This item does not have a Seerr/Jellyfin link.')
        sonarr = (item.get('source') or '').split(':')[0] == 'sonarr'
        provider = 'Tvdb' if sonarr else 'Tmdb'
        info = {}
        if not sonarr:
            details = api.get_movie_details(int(item['external_id'])) if item['kind'] == 'movie' else api.get_tv_details(int(item['external_id']))
            info = details.get('mediaInfo') or {}
        regular = not item.get('is4k') and info.get('status') in (4, 5)
        candidate = info.get('jellyfinMediaId' if regular else 'jellyfinMediaId4k')
        credential = self.store.credential(account['discord_id'])
        jellyfin = JellyfinClient(credential['url'], credential['device_id'], self.config.timeout, credential['token'])
        try:
            matches = jellyfin.request('GET', '/Items', params={'UserId': credential['user_id'], 'Recursive': 'true',
                'IncludeItemTypes': 'Movie' if item['kind'] == 'movie' else 'Series',
                'AnyProviderIdEquals': provider + '.' + str(item['external_id']), 'Fields': 'ProviderIds', 'Limit': 20})
        except VerificationRequired:
            self.store.revoke_credential(account['discord_id'], credential['token'])
            raise
        rows = [row for row in matches['Items'] if
            str((row.get('ProviderIds') or {}).get(provider)) == str(item['external_id']) and row.get('Id')]
        selected = next((row for row in rows if candidate and identity(row['Id']) == identity(candidate)), None)
        if not selected and len(rows) == 1 and int(matches.get('TotalRecordCount', 1)) == 1:
            selected = rows[0]
        if not selected:
            raise UserError('No unique accessible item was found. Ask an operator to refresh availability.')
        url = self.jellyfin_link(account, {'jellyfin_id': identity(selected['Id'])})
        if not url:
            raise UserError('The browser destination is unavailable. Ask an operator to check configuration.')
        return url

    def _op_watches(self, account, api, user):
        self._op_sync_watchlist(account, api, user)
        return self._gallery_posters([dict(w, available=False, subtitle='Followed') for w in self.store.watches(account['discord_id'])])

    async def watches(self, discord_id):
        return await self.run(discord_id, 'watches')

    def _gallery_posters(self, items):
        # Fixed artwork only, not cached availability/permissions. Renderers accept
        # strictly validated TMDB paths and never upstream private image URLs.
        with self.store.connect() as db:
            for item in items:
                if item['kind'] not in ('movie', 'tv') or item.get('poster_path'):
                    continue
                tmdb = json.loads(item['payload']).get('tmdb_id') if item.get('action') == 'delete' else item['external_id']
                row = db.execute('SELECT poster_path FROM media_details WHERE type=? AND tmdb_id=? LIMIT 1', (item['kind'], tmdb)).fetchone()
                if row:
                    item['poster_path'] = row['poster_path']
        return items

    async def unwatch(self, discord_id, item):
        return await self.run(discord_id, 'watch', item, True)

    def _op_import_watchlist(self, account, api, user):
        # Backward-compatible operation for existing clients; it now reconciles.
        return self._op_sync_watchlist(account, api, user)

    def _op_sync_watchlist(self, account, api, user):
        def before_write():
            with self.as_user(account):
                pass  # Recheck Jellyfin and the live Seerr/Discord binding before writes.
        return self.watchlists.sync(account, api, before_write)

    async def sync_watchlists(self):
        # Not tied to DM opt-in or a successful global library/storage refresh.
        # Isolate users: an outage/revoked link must not erase state or stop others.
        with self.store.connect() as db:
            users = [r['discord_id'] for r in db.execute('SELECT discord_id FROM accounts ORDER BY discord_id')]
        slots = asyncio.Semaphore(4)
        async def sync_user(uid):
            try:
                async with slots:
                    await self.run(uid, 'sync_watchlist')
            except Exception:
                log.warning('A linked watchlist could not sync; saved entries and pending changes preserved')
        await asyncio.gather(*(sync_user(uid) for uid in users))

    def _op_preferences(self, account, api, user, opted_in=None, device=None):
        if device is not None and device not in self.devices():
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
        account = await self.verified_account(discord_id)
        return self._op_deletions(account, None, None)

    def _op_deletions(self, account, api, user):
        with self.store.connect() as db:
            rows = db.execute("SELECT * FROM actions WHERE discord_id=? AND action='delete' ORDER BY id DESC", (account['discord_id'],)).fetchall()
            items = [dict(r, available=False, subtitle=f"#{r['id']} · {r['status']} · {'4K' if json.loads(r['payload'])['is4k'] else 'Standard'}") for r in rows]
        return self._gallery_posters(items)

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
        settings = self.jellyfin_settings()
        devices = {'Browser': settings.external_url, **self.config.devices}
        device = account.get('device') or next(iter(devices), None)
        base = devices.get(device) or devices.get('Browser')
        item_id = item.get('jellyfin_id')
        if not base or not item_id:
            return None
        return (f"{base.rstrip('/')}/web/index.html#!/details?id={quote(str(item_id), safe='')}"
            f"&serverId={quote(settings.server_id, safe='')}")

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
            sources = [key for key in snapshots if key.split(':')[0] == source]
            if not sources:
                continue
            service_items = {}
            for raw in [item for key in sources for item in snapshots[key]['items']]:
                item = arr_item(source, raw)
                eid = tv_ids.get(item['external_id']) if source == 'sonarr' else item['external_id']
                if eid:
                    previous = service_items.get((item['kind'], eid))
                    if not previous or item['available']:
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
