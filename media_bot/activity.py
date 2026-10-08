"""Discord Activity OAuth and per-viewer, authenticated API. Never trust SDK user IDs."""
import asyncio
import re
import secrets
import time
from pathlib import Path

import requests
from aiohttp import ClientSession, ClientTimeout, web
from .security import RateLimiter, UserError


class ActivityAPI:
    def __init__(self, bot):
        self.bot, self.config, self.service = bot, bot.config, bot.service
        self.sessions = {}
        self.auth_limiter = RateLimiter(10, 60)
        self.poll_limiter = RateLimiter(20, 60)
        self.admin_read_limiter = RateLimiter(self.config.admin_ui_rate_limit, 60)
        self.oauth_lock = asyncio.Lock()
        self.poster_slots = asyncio.Semaphore(4)
        self.poster_limiter = RateLimiter(240, 60)
        self.posters = {}
        self.assets = Path(__file__).resolve().parent.parent / 'activity' / 'dist'

    def discord_request(self, method, path, *, token=None, data=None):
        headers = {'Authorization': f'Bearer {token}'} if token else {}
        try:
            response = requests.request(method, 'https://discord.com/api/v10/' + path,
                headers=headers, data=data, timeout=self.config.timeout, allow_redirects=False)
            if not 200 <= response.status_code < 300:
                raise UserError('Discord authentication could not be verified. Reopen the Activity.')
            return response.json()
        except (requests.RequestException, ValueError):
            raise UserError('Discord could not be reached. Reopen the Activity later.') from None

    def membership(self, token, guild_id):
        if not self.config.guild_ids:
            return
        if not guild_id or int(guild_id) not in self.config.guild_ids:
            raise UserError('Open this Activity from an allowed Discord server.')
        after = ''
        while True:
            path = 'users/@me/guilds?limit=200' + (f'&after={after}' if after else '')
            guilds = self.discord_request('GET', path, token=token)
            if any(g['id'] == str(guild_id) for g in guilds):
                return
            if len(guilds) < 200:
                break
            after = guilds[-1]['id']
        raise UserError('Your membership in this Discord server could not be verified.')

    def exchange(self, code, guild_id):
        data = self.discord_request('POST', 'oauth2/token', data={
            'client_id': self.config.application_id, 'client_secret': self.config.client_secret,
            'grant_type': 'authorization_code', 'code': code})
        token = data['access_token']
        user = self.discord_request('GET', 'users/@me', token=token)
        self.membership(token, guild_id)
        self.sessions = {k: v for k, v in self.sessions.items() if v['expires'] > time.monotonic()}
        # Bound active sessions, and revoke previous sessions for this same viewer.
        self.sessions = {k: v for k, v in self.sessions.items() if v['discord_id'] != int(user['id'])}
        if len(self.sessions) >= 1000:
            raise UserError('Too many active sessions. Try again later.')
        session_token = secrets.token_urlsafe(32)
        self.sessions[session_token] = {'discord_id': int(user['id']), 'guild_id': guild_id,
            'oauth_token': token, 'expires': time.monotonic() + min(self.config.activity_session_ttl, int(data.get('expires_in', 900)))}
        return {'access_token': token, 'session_token': session_token}

    async def authenticate(self, request):
        self.auth_limiter.check(request.remote)
        data = await request.json()
        if not isinstance(data, dict):
            raise web.HTTPBadRequest()
        code, guild = data.get('code'), data.get('guild_id')
        if not isinstance(code, str) or not 1 <= len(code) <= 512 or guild is not None and (not isinstance(guild, str) or not guild.isdigit()):
            raise web.HTTPBadRequest()
        async with self.oauth_lock:
            result = await self.service.offload(self.exchange, code, guild)
        return web.json_response(result)

    async def session(self, request, polling=False, admin_read=False):
        header = request.headers.get('Authorization', '')
        if not header.startswith('Bearer '):
            raise web.HTTPUnauthorized()
        token = header[7:]
        session = self.sessions.get(token)
        if not session or session['expires'] <= time.monotonic():
            self.sessions.pop(token, None)
            raise web.HTTPUnauthorized()
        limiter = self.poll_limiter if polling else self.admin_read_limiter if admin_read else self.bot.limiter
        limiter.check(session['discord_id'])
        # Recheck server membership against Discord, not a client-provided guild/user ID.
        await self.service.offload(self.membership, session['oauth_token'], session['guild_id'])
        return session

    async def config_route(self, request):
        return web.json_response({'application_id': self.config.application_id,
            'scopes': ['identify', 'guilds'] if self.config.guild_ids else ['identify'],
            'refresh_interval': self.config.activity_refresh_interval, 'sync_interval': self.config.sync_interval})

    async def action(self, request):
        data = await request.json()
        if not isinstance(data, dict):
            raise web.HTTPBadRequest()
        operation = data.get('operation')
        session = await self.session(request, polling=operation == 'link_status', admin_read=operation in ('admin_state', 'admin_inbox'))
        uid = session['discord_id']
        if operation == 'profile':
            admin = await self.service.verify_admin(uid)
            return web.json_response({'account': self.service.store.account(uid), 'devices': list(self.config.devices),
                                      'is_admin': admin})
        if isinstance(operation, str) and operation.startswith('admin_'):
            await self.service.offload(self.bot.admin.require_admin, uid)
            if operation == 'admin_state':
                result = await self.service.offload(self.bot.admin.state, uid)
            elif operation == 'admin_inbox':
                from functools import partial
                result = await self.service.offload(partial(self.bot.admin.inbox, uid, kind=data.get('kind', 'all'),
                    guild_id=data.get('guild_id'), user_id=data.get('user_id'), channel_id=data.get('channel_id'),
                    query=data.get('query', ''), page=positive_id(data.get('page', 1)), order=data.get('order', 'newest')))
            elif operation == 'admin_prepare':
                guild_ids = data.get('guild_ids', [])
                if not isinstance(guild_ids, list) or len(guild_ids) > 1000:
                    raise web.HTTPBadRequest()
                result = await self.bot.admin.prepare(uid, data.get('message'), [snowflake(g) for g in guild_ids],
                    user_id=snowflake(data['user_id']) if data.get('user_id') else None,
                    all_users=data.get('all_users') is True,
                    channel_id=snowflake(data['channel_id']) if data.get('channel_id') else None)
            elif operation == 'admin_send':
                if data.get('confirmed') is not True:
                    raise UserError('Confirm the recipients before sending.')
                result = {'job': await self.service.offload(self.bot.admin.confirm, uid, data.get('plan'))}
            else:
                raise web.HTTPBadRequest()
            return web.json_response({'result': result})
        if operation == 'link_start':
            self.bot.link_limiter.check(uid)
            link = await self.bot.linking.begin(uid)
            return web.json_response({'code': link.code, 'generation': link.generation, 'expires_in': self.config.link_ttl})
        if operation in ('link_status', 'link_cancel'):
            link = self.bot.linking.sessions.get(uid)
            if not link or link.generation != data.get('generation'):
                raise UserError('The connection expired or was replaced. Start again.')
            if operation == 'link_cancel':
                await self.bot.linking.cancel(uid, link.generation)
                return web.json_response({'message': 'Connection cancelled.'})
            try:
                name = await self.bot.linking.check(link)
                if link.state in ('linked', 'expired'):
                    self.bot.linking.sessions.pop(uid, None)
                    await self.bot.linking.close_client(link)
                return web.json_response({'state': link.state, 'name': name if link.state == 'linked' else None})
            except Exception:
                await self.bot.linking.cancel(uid, link.generation)
                raise
        self.service.account(uid)
        if operation == 'status':
            return web.json_response(self.service.store.meta())
        if operation == 'deletions':
            result = await self.service.deletions(uid)
        elif operation == 'watches':
            result = await self.service.watches(uid)
        elif operation == 'undo':
            result = await self.service.cancel_deletion(uid, positive_id(data.get('id')))
        elif operation == 'mute':
            self.service.mute_notifications(uid)
            result = 'All personal updates muted.'
        elif operation in ('dashboard', 'storage', 'import_watchlist'):
            result = await self.service.run(uid, operation)
        elif operation == 'requests':
            result = await self.service.run(uid, operation, data.get('all_requests') is True)
        elif operation == 'library':
            kind = media_kind(data.get('kind', 'all'), allow_all=True)
            result = await self.service.run(uid, operation, kind)
        elif operation == 'search':
            query = data.get('query')
            if not isinstance(query, str) or not 1 <= len(query.strip()) <= 100:
                raise UserError('Enter a search between 1 and 100 characters.')
            result = await self.service.run(uid, operation, media_kind(data.get('kind')), query,
                                            min(500, positive_id(data.get('page', 1))))
        elif operation == 'delete':
            if data.get('confirmed') is not True:
                raise UserError('Explicit confirmation is required to schedule file deletion.')
            result = await self.service.run(uid, operation, positive_id(data.get('id')))
        elif operation in ('request', 'watch', 'open'):
            item = item_reference(data.get('item'))
            if operation == 'watch' and data.get('remove') is True:
                result = await self.service.unwatch(uid, item)
                return web.json_response({'result': result})
            if operation == 'request' and data.get('confirmed') is not True:
                raise UserError('Confirm the request first.')
            args = (item, data.get('remove') is True) if operation == 'watch' else (item,)
            result = await self.service.run(uid, operation, *args)
        elif operation == 'preferences':
            enabled, device = data.get('enabled'), data.get('device')
            if enabled is not None and not isinstance(enabled, bool) or device is not None and not isinstance(device, str):
                raise web.HTTPBadRequest()
            result = await self.service.run(uid, operation, enabled, device)
        else:
            raise web.HTTPBadRequest()
        return web.json_response({'result': public_result(result)})

    async def index(self, request):
        path = self.assets / 'index.html'
        if not path.exists():
            raise UserError('Build the Activity frontend with npm ci && npm run build in activity/.')
        return web.FileResponse(path, headers={'Content-Security-Policy': "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self' data:; object-src 'none'; base-uri 'self'", 'Referrer-Policy': 'no-referrer'})

    async def poster(self, request):
        self.poster_limiter.check(request.remote)
        name = request.match_info['name']
        if not re.fullmatch(r'[A-Za-z0-9_-]{1,100}\.(?:jpg|png|webp)', name):
            raise web.HTTPBadRequest()
        cached = self.posters.get(name)
        if cached and cached[0] > time.monotonic():
            return web.Response(body=cached[1], content_type=cached[2])
        async with self.poster_slots:
            async with ClientSession(timeout=ClientTimeout(total=10)) as client:
                async with client.get('https://image.tmdb.org/t/p/w342/' + name, allow_redirects=False) as response:
                    if response.status != 200 or response.content_type not in ('image/jpeg', 'image/png', 'image/webp'):
                        raise web.HTTPNotFound()
                    chunks, size = [], 0
                    async for chunk in response.content.iter_chunked(64 * 1024):
                        size += len(chunk)
                        if size > 1024 * 1024:
                            raise web.HTTPBadRequest()
                        chunks.append(chunk)
                    body = b''.join(chunks)
                    if len(self.posters) >= 100:
                        self.posters.pop(next(iter(self.posters)))
                    self.posters[name] = (time.monotonic() + 900, body, response.content_type)
                    return web.Response(body=body, content_type=response.content_type)

    def register(self, app):
        app.router.add_get('/', self.index)
        app.router.add_get('/api/activity/config', self.config_route)
        app.router.add_post('/api/activity/auth', self.authenticate)
        app.router.add_post('/api/activity/action', self.action)
        app.router.add_get('/api/activity/poster/{name}', self.poster)
        if (self.assets / 'assets').exists():
            app.router.add_static('/assets/', self.assets / 'assets', show_index=False)


def positive_id(value):
    if isinstance(value, bool) or not isinstance(value, (int, str)) or not str(value).isdigit() or not 0 < int(value) <= 2 ** 53:
        raise UserError('Invalid item ID.')
    return int(value)


def snowflake(value):
    # Discord IDs exceed JavaScript's safe-integer range; transmit them as strings.
    if not isinstance(value, str) or not value.isascii() or not value.isdigit() or not 0 < int(value) < 2 ** 64:
        raise UserError('Invalid Discord ID. Use its copied numeric ID.')
    return int(value)


def media_kind(value, allow_all=False):
    if value not in ('movie', 'tv', 'music', 'book') and not (allow_all and value == 'all'):
        raise UserError('Invalid media category.')
    return value


def item_reference(value):
    if not isinstance(value, dict):
        raise UserError('Select an item first.')
    kind = media_kind(value.get('kind'))
    eid, title, source = value.get('external_id'), value.get('title'), value.get('source')
    if not isinstance(eid, str) or not re.fullmatch(r'[A-Za-z0-9._-]{1,128}', eid) or not isinstance(title, str) or not 1 <= len(title) <= 100:
        raise UserError('Invalid item reference. Search again.')
    if kind in ('movie', 'tv'):
        positive_id(eid)
    if source not in (None, 'seerr', 'radarr', 'sonarr', 'lidarr', 'readarr') and (not isinstance(source, str) or not re.fullmatch(r'(radarr|sonarr):[0-9]+', source)):
        raise UserError('Invalid item source.')
    return {'kind': kind, 'external_id': eid, 'title': title, 'source': source}


def public_result(value):
    if isinstance(value, list):
        fields = ('id', 'kind', 'external_id', 'title', 'subtitle', 'overview', 'available', 'source',
            'size', 'is4k', 'jellyfin_id', 'poster_path', 'status', 'execute_after', 'error', 'deletable', 'requestable')
        return [{key: row[key] for key in fields if key in row} for row in value]
    return value
