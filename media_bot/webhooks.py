"""Optional authenticated service notification receiver. No browser UI/routes."""
import base64
import secrets
import time

from aiohttp import web
from .security import RateLimiter, UserError


class WebhookServer:
    def __init__(self, service, refresh_event, activity=None):
        self.service, self.config, self.refresh_event = service, service.config, refresh_event
        self.limiter = RateLimiter(self.config.webhook_limit, 60)
        self.runner = None
        self.activity = activity
        self.last_tests = {}

    @web.middleware
    async def errors(self, request, handler):
        try:
            response = await handler(request)
        except UserError as exc:
            limited = str(exc).startswith('Too many')
            response = web.json_response({'error': str(exc)}, status=429 if limited else 400)
        except web.HTTPException as exc:
            headers = {key: value for key, value in exc.headers.items() if key.lower() != 'content-type'}
            response = web.json_response({'error': exc.reason}, status=exc.status, headers=headers)
        except Exception:
            response = web.json_response({'error': 'Invalid notification payload'}, status=400)
        response.headers.update({'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff'})
        return response

    def authenticate(self, header):
        secret = self.config.webhook_secret
        if not secret:
            return False
        if header.startswith('Bearer '):
            return secrets.compare_digest(header[7:], secret)
        if header.startswith('Basic '):
            try:
                decoded = base64.b64decode(header[6:], validate=True).decode('utf-8')
                username, password = decoded.split(':', 1)
                return username == 'bot' and secrets.compare_digest(password, secret)
            except (ValueError, UnicodeError):
                return False
        return False

    async def webhook(self, request):
        self.limiter.check(('ip', request.remote))  # Never trust spoofable forwarded IP headers.
        if not self.authenticate(request.headers.get('Authorization', '')):
            raise web.HTTPUnauthorized(headers={'WWW-Authenticate': 'Basic realm="Media Hub"'})
        source = request.match_info['source']
        if source != 'seerr' and source not in {key.split(':')[0] for key in self.service.arr}:
            raise web.HTTPNotFound()
        self.limiter.check(('source', source))
        payload = await request.json()
        if not isinstance(payload, dict):
            raise web.HTTPBadRequest()
        event = payload.get('eventType') or payload.get('notification_type') or payload.get('type')
        if not isinstance(event, str) or not 1 <= len(event) <= 100:
            raise web.HTTPBadRequest()
        if event.lower() != 'test':
            # Payloads only wake a sync; messages/actions come from verified API state.
            self.refresh_event.set()
        result = {'accepted': True}
        if event.lower() == 'test':
            self.last_tests[source] = time.time()
        marker = payload.get('verification')
        if event.lower() == 'test' and isinstance(marker, str) and len(marker) <= 100:
            result['verification'] = marker
        return web.json_response(result)

    def application(self):
        app = web.Application(middlewares=[self.errors], client_max_size=32 * 1024)
        app.router.add_post('/webhooks/{source}', self.webhook)
        if self.activity:
            self.activity.register(app)
        return app

    async def start(self):
        if not self.config.webhook_secret and not self.activity:
            return  # Slash-only mode requires no hosted HTTP service.
        self.runner = web.AppRunner(self.application(), access_log=None)
        await self.runner.setup()
        await web.TCPSite(self.runner, self.config.host, self.config.port).start()

    async def close(self):
        if self.runner:
            await self.runner.cleanup()
