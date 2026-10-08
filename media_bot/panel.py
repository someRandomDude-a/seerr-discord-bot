"""Separate authenticated operator listener: onboarding, settings and bot administration."""
import asyncio
import hashlib
import ipaddress
import json
import os
import secrets
import time
from functools import partial
from pathlib import Path
from urllib.parse import urlsplit, quote

import requests
from aiohttp import web

from seerr import SeerrAPI
from .arr import ArrClient
from .config import ArrConfig, Config, http_url, integer
from .discovery import discover_arr
from .security import RateLimiter, UserError
from .settings import SECRET_KEYS, load_settings, save_settings, settings_path
from .chat import FILE_LIMIT, fetch_asset, image_type

ROOT = Path(__file__).resolve().parent.parent
ASSETS = Path(__file__).resolve().parent / 'panel_assets'


def catalog():
    fields, comments = {}, []
    for line in (ROOT / '.env.advanced.example').read_text(encoding='utf-8').splitlines():
        if line.startswith('#'):
            comments.append(line[1:].strip())
        elif '=' in line:
            key, value = line.split('=', 1)
            if key != 'DATA_DIR':
                # Example credentials/URLs are not operational defaults.
                default = '' if value == 'replace-me' or key in ('SEERR_URL', 'JELLYFIN_DEVICE_URLS') else value
                if key == 'JELLYFIN_DEVICE_URLS':
                    default = '{}'
                fields[key] = {'key': key, 'default': default, 'help': ' '.join(comments), 'secret': key in SECRET_KEYS}
            comments = []
        else:
            comments = []
    return fields


class ControlPanel:
    def __init__(self, host='127.0.0.1', port=0, path=None):
        self.host, self.port = host, port
        self.path = Path(path) if path else settings_path()
        self.code = secrets.token_urlsafe(24)
        self.sessions = {}
        self.limiter = RateLimiter(10, 60)
        self.fields = catalog()
        self.runner = None
        self.bot = None
        self.changed = asyncio.Event()
        self.operation_lock = asyncio.Lock()
        self.runtime_error = ''
        self.media_slots = asyncio.Semaphore(4)
        self.media_limiter = RateLimiter(120, 60)
        self.upload_limiter = RateLimiter(10, 60)
        self.admin_limiter = RateLimiter(240, 60)

    @web.middleware
    async def guard(self, request, handler):
        try:
            # Reject DNS-rebinding hosts. Private IP access is allowed for explicitly published LAN panels.
            hostname = urlsplit('http://' + request.host).hostname
            try:
                private = ipaddress.ip_address(hostname).is_private
            except ValueError:
                private = hostname == 'localhost'
            if not private:
                raise web.HTTPForbidden(reason='Use a local/private IP address for the admin panel')
            if request.method == 'POST':
                if request.content_type != ('multipart/form-data' if request.path == '/api/admin/uploads' else 'application/json'):
                    raise web.HTTPUnsupportedMediaType()
                if request.path != '/api/admin/uploads' and request.content_length and request.content_length > 128 * 1024:
                    raise web.HTTPRequestEntityTooLarge(max_size=128 * 1024, actual_size=request.content_length)
                if request.headers.get('Origin') != 'http://' + request.host:
                    raise web.HTTPForbidden(reason='Same-origin requests required')
            if request.path.startswith('/api/') and request.path != '/api/login':
                self.session(request)
            response = await handler(request)
        except web.HTTPException as exc:
            response = web.json_response({'error': exc.reason}, status=exc.status)
        except (UserError, ValueError, OSError) as exc:
            # Only local validation messages; never include HTTP upstream bodies/credential values.
            message = str(exc) if isinstance(exc, UserError) else 'Settings or storage validation failed. Check values and writable DATA_DIR.'
            response = web.json_response({'error': message}, status=400)
        except Exception:
            response = web.json_response({'error': 'Verification failed. Check connectivity and credentials; no success was confirmed.'}, status=400)
        response.headers.update({'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff',
            'Referrer-Policy': 'no-referrer', 'X-Frame-Options': 'DENY',
            'Content-Security-Policy': "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self' data: blob:; frame-ancestors 'none'; object-src 'none'; base-uri 'none'"})
        if request.path.startswith(('/api/admin/media/', '/api/admin/files/')):
            response.headers['Content-Security-Policy'] = "sandbox; default-src 'none'; frame-ancestors 'none'"
        return response

    def session(self, request):
        token = request.cookies.get('panel_session', '')
        session = self.sessions.get(token)
        if not session or session['expires'] <= time.monotonic() or session['host'] != request.host:
            self.sessions.pop(token, None)
            raise web.HTTPUnauthorized()
        return session

    async def login(self, request):
        self.limiter.check(request.remote)
        data = await request.json()
        code = data.get('code') if isinstance(data, dict) else None
        if not isinstance(code, str) or not self.code or not secrets.compare_digest(code, self.code):
            raise web.HTTPUnauthorized()
        draft = load_settings(self.path)
        self.code = ''  # One exchange per console code; restart the panel if the session is lost.
        token = secrets.token_urlsafe(32)
        self.sessions[token] = {'expires': time.monotonic() + 8 * 3600, 'host': request.host,
            'draft': draft, 'verified': {}}
        response = web.json_response({'ok': True})
        response.set_cookie('panel_session', token, httponly=True, samesite='Strict', max_age=8 * 3600, path='/')
        return response

    def values(self, session):
        defaults = {key: field['default'] for key, field in self.fields.items()}
        return {**defaults, **session['draft'], **os.environ, 'DATA_DIR': str(self.path.parent)}

    def public(self, session):
        values = self.values(session)
        return {'fields': list(self.fields.values()),
            'values': {key: (bool(values.get(key)) if key in SECRET_KEYS else values.get(key, '')) for key in self.fields},
            'locked': [key for key in self.fields if key in os.environ],
            'verified': [step for step, check in session['verified'].items()
                if check[0] == self.digest(values, self.verification_keys(step)) and time.monotonic() - check[1] < 600],
            'running': bool(self.bot and self.bot.is_ready()), 'runtime_error': self.runtime_error,
            'activity_authenticated': bool(self.bot and self.bot.activity and any(
                item['expires'] > time.monotonic() for item in list(self.bot.activity.sessions.values()))),
            'webhook_tests': self.bot.webhooks.last_tests if self.bot else {},
            'saved': self.path.exists()}

    async def state(self, request):
        return web.json_response(self.public(self.session(request)))

    def update(self, session, data):
        changes = data.get('changes', {})
        clear = data.get('clear', [])
        if not isinstance(changes, dict) or not isinstance(clear, list) or any(key not in SECRET_KEYS for key in clear):
            raise UserError('Invalid settings update')
        draft = dict(session['draft'])
        effective = self.values(session)
        for key, value in changes.items():
            if key not in self.fields or not isinstance(value, str) or len(value) > 8192:
                raise UserError('Unknown setting or invalid value')
            if key in SECRET_KEYS and value == '' and key not in clear:
                continue  # Blank password field means keep the saved/environment secret.
            if key in os.environ and value != effective.get(key):
                raise UserError(f'{key} is overridden by the environment. Remove that override first.')
            draft[key] = value
        for key in clear:
            if key in os.environ:
                raise UserError(f'{key} is overridden by the environment')
            draft[key] = ''
        session['draft'] = draft

    @staticmethod
    def digest(values, keys):
        return hashlib.sha256(json.dumps({key: values.get(key, '') for key in keys}, sort_keys=True).encode()).hexdigest()

    @staticmethod
    def direct_config(name, values):
        prefix = name.upper()
        url, key = values.get(prefix + '_URL', ''), values.get(prefix + '_API_KEY', '')
        if bool(url) != bool(key):
            raise UserError(f'Enter both {name.title()} URL and API key, or leave both blank')
        if not url:
            return None
        return ArrConfig(name, http_url(url, prefix + '_URL'), key,
            values.get(prefix + '_ROOT_FOLDER', ''), integer(prefix + '_QUALITY_PROFILE_ID', 0, 0, values),
            integer(prefix + '_METADATA_PROFILE_ID', 0, 0, values))

    def verify(self, operation, values):
        if operation == 'discord':
            token = values.get('DISCORD_TOKEN', '')
            if not token:
                raise UserError('Enter the bot token')
            def discord_get(path):
                response = requests.get('https://discord.com/api/v10/' + path, headers={'Authorization': 'Bot ' + token}, timeout=15, allow_redirects=False)
                if response.status_code != 200:
                    raise UserError('Discord rejected the bot credentials or test request')
                return response.json()
            user = discord_get('users/@me')
            if not user.get('bot'):
                raise UserError('Use a bot token, not a user token')
            application = discord_get('oauth2/applications/@me')
            if values.get('DISCORD_APPLICATION_ID') and values['DISCORD_APPLICATION_ID'] != application['id']:
                raise UserError('Application ID does not match the bot token')
            flags = int(application.get('flags', 0))
            if values.get('ENABLE_MEMBERS_INTENT', 'false').strip().lower() == 'true' and not flags & ((1 << 14) | (1 << 15)):
                raise UserError('Enable Server Members Intent in Discord before enabling all-member DMs')
            if values.get('INBOX_MESSAGE_CONTENT', 'false').strip().lower() == 'true' and not flags & ((1 << 18) | (1 << 19)):
                raise UserError('Enable Message Content Intent in Discord before enabling full inbox content')
            guilds, after = [], ''
            while True:
                batch = discord_get('users/@me/guilds?limit=200' + ('&after=' + after if after else ''))
                guilds.extend(batch)
                if len(batch) < 200:
                    break
                after = batch[-1]['id']
                if len(guilds) > 10000:
                    raise UserError('Too many guilds to verify in one setup operation')
            selected = set(filter(None, (g.strip() for g in values.get('ALLOWED_GUILD_IDS', '').split(','))))
            if selected - {g['id'] for g in guilds}:
                raise UserError('Invite the bot to every selected server before verifying')
            for exception in filter(None, (g.strip() for g in values.get('ADMIN_DISCORD_IDS', '').split(','))):
                if not exception.isascii() or not exception.isdigit() or not 0 < int(exception) < 2 ** 64:
                    raise UserError('Invalid bot-only operator ID')
                if discord_get('users/' + exception).get('bot'):
                    raise UserError('Operator exceptions must be human users')
            return {'application_id': application['id'], 'bot': user['username'],
                'guilds': [{'id': g['id'], 'name': g['name']} for g in guilds],
                'install_url': 'https://discord.com/oauth2/authorize?client_id=' + application['id'] + '&scope=bot%20applications.commands&permissions=84992'}
        if operation == 'seerr':
            url = http_url(values.get('SEERR_URL', ''), 'SEERR_URL')
            if not values.get('SEERR_ADMIN_KEY'):
                raise UserError('Enter the Seerr API key')
            api = SeerrAPI(url, values['SEERR_ADMIN_KEY'], timeout=15)
            try:
                user = api.get_current_user()
                if user.get('id') != 1 and not int(user.get('permissions', 0)) & 2:
                    raise UserError('Seerr administrator API access is required')
                configs = discover_arr(api) if values.get('SEERR_DISCOVERY', 'true').strip().lower() == 'true' else {}
                if values.get('SEERR_DISCOVERY', 'true').strip().lower() == 'false':
                    configs = {name: config for name in ('radarr', 'sonarr')
                        if (config := self.direct_config(name, values))}
                for config in configs.values():
                    client = ArrClient(config, 15)
                    client.request('GET', 'system/status')
                    client.request('GET', 'diskspace')
                users, skip = [], 0
                while True:
                    batch = api.list_users(take=100, skip=skip)['results']
                    users.extend(batch)
                    if len(batch) < 100:
                        break
                    skip += 100
                    if skip > 10000:
                        raise UserError('Too many Seerr users for onboarding')
                return {'services': [{'name': key, 'url': config.url} for key, config in configs.items()],
                    'admins': [{'id': u['id'], 'name': u.get('displayName') or u.get('username') or u.get('email') or str(u['id'])}
                        for u in users if u['id'] == 1 or int(u.get('permissions', 0)) & 2],
                    'message': 'Seerr and imported services verified. Admins must use /link to prove their Discord identity.'}
            finally:
                api.close()
        if operation in ('lidarr', 'readarr'):
            prefix = operation.upper()
            if not values.get(prefix + '_URL') and not values.get(prefix + '_API_KEY'):
                return {'disabled': True}
            config = self.direct_config(operation, values)
            client = ArrClient(config, integer('API_TIMEOUT_SECONDS', 15, values=values))
            client.request('GET', 'system/status')
            choices = {path: client.request('GET', path) for path in ('qualityprofile', 'metadataprofile', 'rootfolder')}
            for suffix, collection, key in (('_QUALITY_PROFILE_ID', 'qualityprofile', 'id'),
                ('_METADATA_PROFILE_ID', 'metadataprofile', 'id'), ('_ROOT_FOLDER', 'rootfolder', 'path')):
                value = values.get(prefix + suffix, '')
                if value and value != '0' and value not in {str(row[key]) for row in choices[collection]}:
                    raise UserError(f'{prefix + suffix} is not configured in the service')
            return {'choices': {path: [{key: row[key] for key in ('id', 'name', 'path') if key in row} for row in rows]
                for path, rows in choices.items()},
                'message': 'Connection verified. Choose all three profiles/root to enable additions; browsing needs only the connection.'}
        raise UserError('Unknown verification step')

    def verification_keys(self, operation):
        if operation == 'discord':
            return ['DISCORD_TOKEN', 'DISCORD_APPLICATION_ID', 'ALLOWED_GUILD_IDS', 'ADMIN_DISCORD_IDS', 'ENABLE_MEMBERS_INTENT', 'INBOX_MESSAGE_CONTENT']
        if operation == 'seerr':
            return ['SEERR_URL', 'SEERR_ADMIN_KEY', 'SEERR_DISCOVERY', 'RADARR_URL', 'RADARR_API_KEY', 'SONARR_URL', 'SONARR_API_KEY']
        return [key for key in self.fields if key.startswith(operation.upper() + '_')]

    async def action(self, request):
        session = self.session(request)
        data = await request.json()
        if not isinstance(data, dict):
            raise web.HTTPBadRequest()
        async with self.operation_lock:
            self.update(session, data)
            values = self.values(session)
            operation = data.get('operation')
            if operation in ('discord', 'seerr', 'lidarr', 'readarr'):
                result = await asyncio.to_thread(self.verify, operation, values)
                if operation == 'discord' and not values.get('DISCORD_APPLICATION_ID'):
                    session['draft']['DISCORD_APPLICATION_ID'] = result['application_id']
                    values = self.values(session)
                session['verified'][operation] = (self.digest(values, self.verification_keys(operation)), time.monotonic())
            elif operation == 'save':
                config = Config.from_values(values)
                required = ['discord', 'seerr'] + [name for name in ('lidarr', 'readarr') if name in config.arr]
                for step in required:
                    check = session['verified'].get(step)
                    if not check or check[0] != self.digest(values, self.verification_keys(step)) or time.monotonic() - check[1] >= 600:
                        raise UserError(f'Verify {step} with the current settings before saving')
                if config.activity_enabled and not (ROOT / 'activity/dist/index.html').exists():
                    raise UserError('Build the Activity frontend first: npm ci && npm run build in activity/')
                await asyncio.to_thread(save_settings, session['draft'], self.path)
                self.changed.set()
                result = {'message': 'Saved. Starting/restarting the bot; verify hosting after it connects.'}
            elif operation == 'generate_webhook':
                if 'WEBHOOK_SECRET' in os.environ:
                    raise UserError('Webhook secret is overridden by the environment')
                session['draft']['WEBHOOK_SECRET'] = secrets.token_urlsafe(32)
                result = {'secret': session['draft']['WEBHOOK_SECRET'], 'message': 'Copy this credential to your services, then save. This does not configure Seerr/Servarr notifications automatically.'}
            elif operation == 'webhook_info':
                if not values.get('WEBHOOK_SECRET'):
                    raise UserError('Generate a webhook credential first')
                result = {'secret': values['WEBHOOK_SECRET'], 'message': 'Current webhook credential. Use Bearer, or Basic username bot/password this value. Never share it.'}
            elif operation == 'public_test':
                result = await self.public_test(values)
            else:
                raise UserError('Unknown panel operation')
            response = self.public(session)
            response['result'] = result
            return web.json_response(response)

    async def public_test(self, values):
        bot = self.bot
        if not bot or not bot.is_ready():
            raise UserError('Save and wait for the bot to connect first')
        if values.get('WEBHOOK_PUBLIC_URL', '').rstrip('/') != bot.config.webhook_public_url or values.get('WEBHOOK_SECRET') != bot.config.webhook_secret:
            raise UserError('Save webhook URL/credential changes before testing the receiver')
        if (values.get('ACTIVITY_ENABLED', 'false').strip().lower() == 'true') != bot.config.activity_enabled or values.get('DISCORD_APPLICATION_ID', '') != bot.config.application_id:
            raise UserError('Save Activity changes and wait for the bot to restart before testing')
        url = http_url(values.get('WEBHOOK_PUBLIC_URL', ''), 'WEBHOOK_PUBLIC_URL')
        if not url.startswith('https://'):
            raise UserError('Public verification requires HTTPS. Local/private webhooks can be tested through the native service Test buttons.')
        def test():
            results = {}
            if bot.config.activity_enabled:
                response = requests.get(url + '/api/activity/config', timeout=15, allow_redirects=False)
                if response.status_code != 200 or response.json().get('application_id') != bot.config.application_id:
                    raise UserError('The HTTPS hostname did not serve this Activity backend')
                results['activity'] = 'HTTPS endpoint verified. Launch /activity in Discord to verify URL Mapping/OAuth; an HTTP test cannot prove that.'
            if bot.config.webhook_secret:
                marker = secrets.token_urlsafe(16)
                response = requests.post(url + '/webhooks/seerr', json={'notification_type': 'Test', 'verification': marker},
                    headers={'Authorization': 'Bearer ' + bot.config.webhook_secret}, timeout=15, allow_redirects=False)
                if response.status_code != 200 or response.json().get('verification') != marker:
                    raise UserError('The webhook endpoint did not confirm the authenticated test')
                results['webhook'] = 'Authenticated HTTPS receiver verified. Test each service in its native settings to verify sender connectivity.'
            if not results:
                raise UserError('Enable Activity or generate a webhook credential before a public test')
            return results
        return await asyncio.to_thread(test)

    async def admin_action(self, request):
        self.session(request)  # Local code grants operator authority; never infer it from a browser actor ID.
        self.admin_limiter.check(request.cookies.get('panel_session'))
        bot = self.bot
        if not bot or not bot.is_ready():
            raise UserError('Bot is not connected yet')
        data = await request.json()
        if not isinstance(data, dict):
            raise web.HTTPBadRequest()
        operation = data.get('operation')
        actor = 'local-panel'
        if operation == 'state':
            result = await bot.service.offload(bot.admin.state, actor)
        elif operation == 'inbox':
            result = await bot.service.offload(partial(bot.admin.inbox, actor, kind=data.get('kind', 'all'),
                guild_id=data.get('guild_id'), user_id=data.get('user_id'), channel_id=data.get('channel_id'),
                query=data.get('query', ''), order=data.get('order', 'newest'), page=data.get('page', 1)))
        elif operation == 'prepare':
            from .activity import snowflake
            guilds = data.get('guild_ids', [])
            if not isinstance(guilds, list) or len(guilds) > 1000:
                raise web.HTTPBadRequest()
            result = await bot.admin.prepare(actor, data.get('message', ''), [snowflake(g) for g in guilds],
                user_id=snowflake(data['user_id']) if data.get('user_id') else None, all_users=data.get('all_users') is True,
                channel_id=snowflake(data['channel_id']) if data.get('channel_id') else None, uploads=data.get('uploads', []))
        elif operation == 'chat':
            from .activity import snowflake
            result = {'job': await bot.admin.chat(actor, data.get('message', ''),
                user_id=snowflake(data['user_id']) if data.get('user_id') else None,
                guild_id=snowflake(data['guild_id']) if data.get('guild_id') else None,
                channel_id=snowflake(data['channel_id']) if data.get('channel_id') else None,
                uploads=data.get('uploads', []), request_id=data.get('request_id'))}
        elif operation == 'history':
            result = await bot.service.offload(bot.admin.history, actor, data.get('page', 1))
        elif operation == 'send':
            if data.get('confirmed') is not True:
                raise UserError('Confirm the preview before sending')
            result = {'job': await bot.service.offload(bot.admin.confirm, actor, data.get('plan'))}
        elif operation == 'deliveries':
            from .activity import positive_id
            result = await bot.service.offload(bot.admin.deliveries, actor,
                positive_id(data.get('job')), positive_id(data.get('page', 1)))
        else:
            raise web.HTTPBadRequest()
        return web.json_response({'result': result})

    def connected_admin(self, request):
        self.session(request)
        if not self.bot or not self.bot.is_ready():
            raise UserError('Bot is not connected yet')
        return self.bot.admin

    async def upload(self, request):
        admin = self.connected_admin(request)
        self.upload_limiter.check(request.cookies.get('panel_session'))
        reader = await request.multipart()
        part = await reader.next()
        if not part or part.name != 'file' or not part.filename:
            raise web.HTTPBadRequest(reason='Attach one file')
        chunks, size = [], 0
        while True:
            chunk = await part.read_chunk(64 * 1024)
            if not chunk:
                break
            size += len(chunk)
            if size > FILE_LIMIT:
                raise UserError('Each file is limited to 8 MiB.')
            chunks.append(chunk)
        if await reader.next() is not None:
            raise UserError('Upload one file per request.')
        self.session(request)
        result = await asyncio.to_thread(admin.files.upload, 'local-panel', part.filename, b''.join(chunks))
        return web.json_response({'result': result})

    async def media(self, request):
        admin = self.connected_admin(request)
        self.media_limiter.check(request.cookies.get('panel_session'))
        if 'file' in request.match_info:
            with admin.store.connect() as db:
                row = db.execute('SELECT * FROM admin_files WHERE id=? AND expires>?', (request.match_info['file'], time.time())).fetchone()
            if not row or row['actor'] != 'local-panel' and row['announcement_id'] is None:
                raise web.HTTPNotFound()
            body, filename = row['body'], row['filename']
        else:
            with admin.store.connect() as db:
                row = db.execute('SELECT * FROM inbox WHERE message_id=? AND deleted=0', (request.match_info['message'],)).fetchone()
            if not row or row['created_at'] < time.time() - admin.config.inbox_retention_days * 86400:
                raise web.HTTPNotFound()
            asset = json.loads(row['rich']).get('assets', {}).get(request.match_info['asset'])
            if not asset:
                raise web.HTTPNotFound()
            async with self.media_slots:
                try:
                    body = await fetch_asset(asset['url'])
                except UserError:
                    # Discord signs attachment URLs. Refresh only this retained
                    # message's known channel/ID; never fetch a client-supplied URL.
                    channel = admin.bot.get_channel(int(row['channel_id'])) or await admin.bot.fetch_channel(int(row['channel_id']))
                    message = await channel.fetch_message(int(row['message_id']))
                    await admin.edited(message, mark_edited=False)
                    from .chat import metadata
                    asset = metadata(message).get('assets', {}).get(request.match_info['asset'])
                    if not asset:
                        raise web.HTTPNotFound()
                    body = await fetch_asset(asset['url'])
            filename = asset['filename']
            # A delete/retention prune may arrive while CDN I/O is in flight.
            with admin.store.connect() as db:
                retained = db.execute('SELECT 1 FROM inbox WHERE id=? AND deleted=0 AND created_at>?',
                    (row['id'], time.time() - admin.config.inbox_retention_days * 86400)).fetchone()
            if not retained:
                raise web.HTTPNotFound()
        self.session(request)
        kind = image_type(body)
        return web.Response(body=body, content_type=kind, headers={
            'Content-Disposition': ("inline" if kind.startswith('image/') else 'attachment') + "; filename*=UTF-8''" + quote(filename, safe=''),
            'Content-Security-Policy': "sandbox; default-src 'none'"})

    async def events(self, request):
        admin = self.connected_admin(request)
        queue = admin.subscribe()
        response = web.StreamResponse(headers={'Content-Type': 'text/event-stream', 'Cache-Control': 'no-store',
            'X-Accel-Buffering': 'no', 'X-Content-Type-Options': 'nosniff'})
        try:
            await response.prepare(request)
            while self.bot and self.bot.admin is admin and self.bot.is_ready():
                self.session(request)  # Recheck expiry/host at least every heartbeat.
                try:
                    revision = await asyncio.wait_for(queue.get(), timeout=15)
                    self.session(request)
                    await response.write(f'event: update\ndata: {{"revision":{revision}}}\n\n'.encode())
                    await asyncio.sleep(1)  # Coalesce busy channels; at most one browser reload per second.
                except asyncio.TimeoutError:
                    await response.write(b': heartbeat\n\n')
        except (ConnectionError, web.HTTPUnauthorized):
            pass
        finally:
            admin.subscribers.discard(queue)
        return response

    def application(self):
        app = web.Application(middlewares=[self.guard], client_max_size=10 * 1024 * 1024)
        app.router.add_post('/api/login', self.login)
        app.router.add_get('/api/state', self.state)
        app.router.add_post('/api/action', self.action)
        app.router.add_post('/api/admin', self.admin_action)
        app.router.add_post('/api/admin/uploads', self.upload)
        app.router.add_get('/api/admin/events', self.events)
        app.router.add_get('/api/admin/files/{file}', self.media)
        app.router.add_get('/api/admin/media/{message}/{asset}', self.media)
        async def asset(request):
            files = {'/': ASSETS / 'index.html', '/panel.js': ASSETS / 'panel.js',
                '/panel.css': ASSETS / 'panel.css', '/chat.js': ASSETS / 'chat.js', '/messenger.js': ASSETS / 'messenger.js', '/glass.css': ROOT / 'activity/src/style.css'}
            return web.FileResponse(files[request.path])
        for path in ('/', '/panel.js', '/panel.css', '/chat.js', '/messenger.js', '/glass.css'):
            app.router.add_get(path, asset)
        return app

    async def start(self):
        self.runner = web.AppRunner(self.application(), access_log=None)
        await self.runner.setup()
        site = web.TCPSite(self.runner, self.host, self.port)
        await site.start()
        self.port = self.runner.addresses[0][1]
        return self.port

    async def close(self):
        self.sessions.clear()
        self.code = ''
        if self.runner:
            await self.runner.cleanup()
