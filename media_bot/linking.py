"""Passwordless account verification entirely through Discord + Jellyfin Quick Connect."""
import asyncio
import re
import secrets
import time
from dataclasses import dataclass, field

from seerr import SeerrAPI
from .security import UserError


@dataclass
class LinkSession:
    discord_id: int
    client: object = field(repr=False)
    secret: str = field(repr=False)
    code: str = field(repr=False)
    expires: float
    generation: str = field(default_factory=lambda: secrets.token_urlsafe(16))
    lock: object = field(default_factory=asyncio.Lock, repr=False)
    task: object = field(default=None, repr=False)
    interaction: object = field(default=None, repr=False)
    state: str = 'pending'
    authenticated: bool = False
    closed: bool = False


class QuickConnect:
    def __init__(self, service):
        self.service, self.config = service, service.config
        self.sessions = {}
        self.start_lock = asyncio.Lock()

    async def begin(self, discord_id):
        async with self.start_lock:
            await self.expire()
            await self.cancel(discord_id)
            if len(self.sessions) >= 100:
                raise UserError('Too many active sign-ins. Try again shortly.')
            client = SeerrAPI(self.config.seerr_url, timeout=self.config.timeout)
            try:
                data = await self.service.offload(client._request, 'POST', '/auth/jellyfin/quickconnect/initiate')
                code, secret = str(data['code']), str(data['secret'])
                if not code.isdigit() or not 4 <= len(code) <= 12 or not re.fullmatch(r'[A-Fa-f0-9]{8,128}', secret):
                    raise ValueError('Invalid Quick Connect response')
                session = LinkSession(discord_id, client, secret, code, time.monotonic() + self.config.link_ttl)
                self.sessions[discord_id] = session
                return session
            except BaseException as exc:
                client.close()
                if isinstance(exc, asyncio.CancelledError):
                    raise
                raise UserError('Quick Connect could not start. Enable Quick Connect in Jellyfin and use a Seerr version supporting Jellyfin Quick Connect authentication.') from None

    def attach(self, session, interaction):
        session.interaction = interaction
        session.task = asyncio.create_task(self.poll(session))

    async def check(self, session):
        async with session.lock:
            if session.closed:
                return session.state
            if self.sessions.get(session.discord_id) is not session or session.state != 'pending':
                return session.state
            if time.monotonic() >= session.expires:
                session.state = 'expired'
                return session.state
            data = await self.service.offload(session.client._request, 'GET', '/auth/jellyfin/quickconnect/check', None, {'secret': session.secret})
            if not data.get('authenticated'):
                return 'pending'
            # Exchange only the approved secret for a Seerr cookie session; never the admin key.
            await self.service.offload(session.client._request, 'POST', '/auth/jellyfin/quickconnect/authenticate', {'secret': session.secret})
            session.authenticated = True
            async with self.service.lock:
                name = await self.service.offload(self.service.link_authenticated, session.discord_id, session.client)
            session.state = 'linked'
            return name

    async def poll(self, session):
        from .ui import clean, embed
        try:
            while session.state == 'pending':
                await asyncio.sleep(self.config.link_poll_interval)
                name = await self.check(session)
                if session.state == 'linked':
                    await session.interaction.edit_original_response(embed=embed('✅ You’re connected', f'Welcome, **{clean(name)}**. Your verified Discord ID is saved in Seerr.\nOpen /dashboard to explore your media. Notifications are off until you opt in.'), view=None)
                    return
            if session.state == 'expired':
                await session.interaction.edit_original_response(embed=embed('Connection code expired', 'No account was linked. Run /link for a new code.'), view=None)
        except asyncio.CancelledError:
            raise
        except Exception as exc:
            session.state = 'failed'
            try:
                message = str(exc) if isinstance(exc, UserError) else 'Quick Connect verification failed. Run /link again; check Seerr and Jellyfin connectivity.'
                await session.interaction.edit_original_response(embed=embed('Unable to connect', message), view=None)
            except Exception:
                pass
        finally:
            if self.sessions.get(session.discord_id) is session:
                self.sessions.pop(session.discord_id, None)
            await self.close_client(session)

    async def close_client(self, session):
        # Never close a cookie session while its worker is still in flight.
        async with session.lock:
            if session.closed:
                return
            if session.authenticated:
                try:
                    await self.service.offload(session.client.logout_user)
                except Exception:
                    pass
            session.client.close()
            session.secret = ''
            session.closed = True

    async def cancel(self, discord_id, generation=None):
        session = self.sessions.get(discord_id)
        if not session or generation is not None and session.generation != generation:
            return False
        # Prevent cancellation from misreporting a completed account link.
        async with session.lock:
            if session.state == 'linked':
                return False
            session.state = 'cancelled'
        self.sessions.pop(discord_id, None)
        if session.task:
            session.task.cancel()
            try:
                await session.task
            except asyncio.CancelledError:
                pass
        else:
            await self.close_client(session)
        return True

    async def close(self):
        for discord_id in list(self.sessions):
            session = self.sessions.get(discord_id)
            cancelled = await self.cancel(discord_id)
            if not cancelled and session and session.task:
                await session.task
            elif not cancelled and session:
                self.sessions.pop(discord_id, None)
                await self.close_client(session)

    async def expire(self):
        for discord_id, session in list(self.sessions.items()):
            if not session.task and time.monotonic() >= session.expires:
                await self.cancel(discord_id)
