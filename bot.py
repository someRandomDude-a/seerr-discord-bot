import asyncio
import logging
import os
import sqlite3
import time

import discord
from discord import app_commands
from discord.ext import commands
from dotenv import load_dotenv

from media_bot.config import Config
from media_bot.security import RateLimiter, UserError
from media_bot.service import MediaService
from media_bot.linking import QuickConnect
from media_bot.activity import ActivityAPI
from media_bot.admin import AdminMessaging
from media_bot.ui import DashboardView, ItemsView, PreferencesView, clean, dashboard_embed, embed, error_message
from media_bot.webhooks import WebhookServer

log = logging.getLogger(__name__)


class SeerrBot(commands.Bot):
    def __init__(self, config):
        # Privileged intents are opt-in; DMs and direct mentions work without message content.
        intents = discord.Intents.default()
        intents.members = config.members_intent
        intents.message_content = config.inbox_message_content
        super().__init__(command_prefix=commands.when_mentioned, intents=intents,
                         allowed_mentions=discord.AllowedMentions.none())
        self.config = config
        self.service = MediaService(config)
        self.limiter = RateLimiter(config.rate_limit, config.rate_window)
        self.link_limiter = RateLimiter(config.login_limit, config.login_window)
        self.refresh_event = asyncio.Event()
        self.linking = QuickConnect(self.service)
        self.admin = AdminMessaging(self)
        self.activity = ActivityAPI(self) if config.activity_enabled else None
        self.webhooks = WebhookServer(self.service, self.refresh_event, self.activity)
        self.worker = None
        self.message_worker = None
        self.tree.on_error = self.command_error
        register_commands(self)

    def guard(self, interaction):
        if self.config.guild_ids and interaction.guild_id not in self.config.guild_ids:
            raise UserError('This bot is restricted to configured Discord servers.')
        self.limiter.check(interaction.user.id)

    async def command_error(self, interaction, error):
        original = getattr(error, 'original', error)
        await error_message(interaction, str(original) if isinstance(original, UserError) else 'Operation not confirmed. Check /status and refresh before retrying.')
        if not isinstance(original, UserError):
            log.warning('Slash command failed (%s)', type(original).__name__)

    async def setup_hook(self):
        if self.config.activity_enabled and self.application_id != int(self.config.application_id):
            raise ValueError('DISCORD_APPLICATION_ID must match the bot token application')
        await self.webhooks.start()
        # Interrupted writes must not be automatically replayed on restart.
        with self.service.store.connect() as db:
            db.execute("UPDATE actions SET status='uncertain',error='Process stopped during execution; verify upstream' WHERE status='processing'")
        if self.config.guild_ids:
            for guild_id in self.config.guild_ids:
                guild = discord.Object(id=guild_id)
                self.tree.copy_global_to(guild=guild)
                await self.tree.sync(guild=guild)
        else:
            await self.tree.sync()
        self.worker = asyncio.create_task(self.sync_worker())
        self.message_worker = asyncio.create_task(self.admin.deliver())

    async def on_ready(self):
        log.info('Media Hub connected to Discord')
        await self.change_presence(activity=discord.Activity(type=discord.ActivityType.watching, name='/dashboard · your media hub'))

    async def on_message(self, message):
        await self.admin.receive(message)

    async def sync_worker(self):
        await self.wait_until_ready()
        while not self.is_closed():
            self.refresh_event.clear()
            try:
                await self.linking.expire()
                await self.service.refresh()
                await self.service.process_due_deletions()
                await self.deliver_notifications()
            except UserError:
                log.warning('Live sync failed; stale-data operations and notifications blocked')
            except Exception as exc:
                log.warning('Background cycle failed (%s)', type(exc).__name__)
            try:
                await asyncio.wait_for(self.refresh_event.wait(), timeout=self.config.sync_interval)
            except asyncio.TimeoutError:
                pass

    def notification_eligible(self, discord_id):
        account = self.service.account(discord_id)
        if not account['opted_in']:
            return False
        with self.service.as_user(account):
            return True

    async def deliver_notifications(self):
        with self.service.store.connect() as db:
            rows = [dict(r) for r in db.execute('SELECT * FROM outbox WHERE sent=0 AND next_attempt<=? ORDER BY id LIMIT 25', (time.time(),))]
        for row in rows:
            try:
                # Revalidate opt-in and live Seerr account immediately before every delivery.
                async with self.service.lock:
                    if self.service.store.meta().get('error'):
                        return
                    if not await asyncio.to_thread(self.notification_eligible, row['discord_id']):
                        with self.service.store.connect() as db:
                            db.execute('UPDATE outbox SET sent=1 WHERE id=?', (row['id'],))
                        continue
                    user = self.get_user(int(row['discord_id'])) or await self.fetch_user(int(row['discord_id']))
                    await user.send(embed=embed(f"✨ {clean(row['title'], 180)}", clean(row['body'], 1400) + '\n\nManage your watchlist and opt-in with /dashboard.'), allowed_mentions=discord.AllowedMentions.none())
                    with self.service.store.connect() as db:
                        db.execute('UPDATE outbox SET sent=1 WHERE id=?', (row['id'],))
            except (discord.Forbidden, UserError):
                # Closed DMs or revoked links: pause safely; user can explicitly opt in again.
                self.service.store.preference(row['discord_id'], opted_in=False)
            except Exception:
                attempts = row['attempts'] + 1
                with self.service.store.connect() as db:
                    db.execute('UPDATE outbox SET attempts=?,next_attempt=? WHERE id=?',
                               (attempts, time.time() + min(3600, 30 * 2 ** min(attempts, 7)), row['id']))

    async def close(self):
        if self.message_worker:
            self.message_worker.cancel()
            try:
                await self.message_worker
            except asyncio.CancelledError:
                pass
        if self.worker:
            self.worker.cancel()
            try:
                await self.worker
            except asyncio.CancelledError:
                pass
        await self.linking.close()
        await self.webhooks.close()
        # Let outstanding to_thread API calls finish before closing their shared session.
        async with self.service.lock:
            self.service.api.close()
        await super().close()


def register_commands(bot):
    @bot.tree.command(name='announce', description='Operators: message this server, a chosen user/server, or an explicit broadcast')
    async def announce(interaction: discord.Interaction, message: str, server_id: str = None,
                       recipient: discord.User = None, all_servers: bool = False, all_users: bool = False,
                       server_ids: str = None, channel_id: str = None):
        bot.guard(interaction)
        await interaction.response.defer(ephemeral=True)
        await bot.service.offload(bot.admin.require_admin, interaction.user.id)
        if all_servers and (server_id or server_ids or recipient):
            raise UserError('Choose all servers or one explicit server/user.')
        if recipient and all_users:
            raise UserError('Choose one user or all members.')
        if server_id and not server_id.isdigit():
            raise UserError('Invalid server ID.')
        if server_ids and server_id:
            raise UserError('Use server_id or server_ids, not both.')
        from media_bot.activity import snowflake
        selected = [snowflake(g.strip()) for g in server_ids.split(',')] if server_ids else None
        guild_ids = selected if selected is not None else [g.id for g in bot.guilds] if all_servers else ([int(server_id)] if server_id else
                      [interaction.guild_id] if interaction.guild_id and not recipient else [])
        preferred = snowflake(channel_id) if channel_id else interaction.channel_id if guild_ids == [interaction.guild_id] and not recipient and not all_servers else None
        plan = await bot.admin.prepare(interaction.user.id, message, guild_ids,
             user_id=recipient.id if recipient else None, all_users=all_users, channel_id=preferred)
        from media_bot.ui import AnnouncementConfirmView
        view = AnnouncementConfirmView(bot, interaction.user.id, plan)
        await interaction.followup.send(embed=view.render(), view=view, ephemeral=True)

    @bot.tree.command(name='inbox', description='Operators: private DMs, bot mentions and replies with filters')
    @app_commands.choices(kind=[app_commands.Choice(name=n, value=v) for n, v in [('All', 'all'), ('DMs', 'dm'), ('Servers', 'guild')]],
                          order=[app_commands.Choice(name=n, value=v) for n, v in [('Newest', 'newest'), ('Oldest', 'oldest')]])
    async def inbox(interaction: discord.Interaction, kind: str = 'all', server_id: str = None,
                    user_id: str = None, channel_id: str = None, query: str = '', order: str = 'newest',
                    page: app_commands.Range[int, 1, 10000] = 1):
        bot.guard(interaction)
        await interaction.response.defer(ephemeral=True)
        await bot.service.offload(bot.admin.require_admin, interaction.user.id)
        from media_bot.admin_ui import AdminListView
        view = AdminListView(bot, interaction.user.id, 'inbox', {'kind': kind, 'guild_id': server_id,
            'user_id': user_id, 'channel_id': channel_id, 'query': query, 'order': order}, page)
        await view.load()
        await interaction.followup.send(embed=view.render(), view=view, ephemeral=True)

    @bot.tree.command(name='servers', description='Operators: joined servers and announcement destinations')
    async def servers(interaction: discord.Interaction, page: app_commands.Range[int, 1, 10000] = 1):
        bot.guard(interaction)
        await interaction.response.defer(ephemeral=True)
        await bot.service.offload(bot.admin.require_admin, interaction.user.id)
        from media_bot.admin_ui import AdminListView
        view = AdminListView(bot, interaction.user.id, 'servers', page=page)
        await view.load()
        await interaction.followup.send(embed=view.render(), view=view, ephemeral=True)

    @bot.tree.command(name='deliveries', description='Operators: announcement results; select a job for individual destinations')
    async def deliveries(interaction: discord.Interaction, job: app_commands.Range[int, 1] = None,
                         page: app_commands.Range[int, 1, 10000] = 1):
        bot.guard(interaction)
        await interaction.response.defer(ephemeral=True)
        await bot.service.offload(bot.admin.require_admin, interaction.user.id)
        from media_bot.admin_ui import AdminListView
        view = AdminListView(bot, interaction.user.id, 'deliveries', page=page, job=job)
        await view.load()
        await interaction.followup.send(embed=view.render(), view=view, ephemeral=True)

    @bot.tree.command(name='activity', description='Launch the Media Hub dashboard inside Discord')
    async def activity(interaction: discord.Interaction):
        bot.guard(interaction)
        if not bot.config.activity_enabled:
            raise UserError('Activity mode is not configured yet. Use /dashboard, or enable it in the Discord Developer Portal and bot environment.')
        await interaction.response.launch_activity()

    @bot.tree.command(name='link', description='Passwordless Jellyfin Quick Connect in Discord; save your verified Discord ID in Seerr')
    async def link(interaction: discord.Interaction):
        bot.guard(interaction)
        bot.link_limiter.check(interaction.user.id)
        await interaction.response.defer(ephemeral=True)
        session = await bot.linking.begin(interaction.user.id)
        from media_bot.ui import QuickConnectView
        view = QuickConnectView(bot, interaction.user.id, session)
        await interaction.edit_original_response(embed=embed('🔐 Connect with Jellyfin',
            f'# `{session.code}`\n'
            '**Jellyfin → Settings → Quick Connect**\n'
            f'Approve this code. Verified automatically.\n**{bot.config.link_ttl}s** · Keep private'), view=view)
        bot.linking.attach(session, interaction)

    @bot.tree.command(name='dashboard', description='Your private media hub: library, requests, storage and notifications')
    async def dashboard(interaction: discord.Interaction):
        bot.guard(interaction)
        await interaction.response.defer(ephemeral=True)
        data = await bot.service.run(interaction.user.id, 'dashboard')
        await interaction.followup.send(embed=dashboard_embed(data), view=DashboardView(bot, interaction.user.id), ephemeral=True)

    @bot.tree.command(name='requests', description='Browse your requests, or all requests with Seerr management permission')
    async def requests(interaction: discord.Interaction, all_requests: bool = False, query: str = ''):
        bot.guard(interaction)
        await interaction.response.defer(ephemeral=True)
        view = ItemsView(bot, interaction.user.id, 'requests', (all_requests,), 'All requests' if all_requests else 'My requests', query=query)
        await view.load()
        await interaction.followup.send(embed=view.render(), view=view, ephemeral=True)

    @bot.tree.command(name='search', description='Discover movies, series, music albums or books and request them')
    @app_commands.choices(kind=[app_commands.Choice(name=label, value=value) for label, value in [('Movies', 'movie'), ('Series', 'tv'), ('Music albums', 'music'), ('Books', 'book')]])
    async def search(interaction: discord.Interaction, kind: app_commands.Choice[str], query: str, page: app_commands.Range[int, 1, 500] = 1):
        bot.guard(interaction)
        await interaction.response.defer(ephemeral=True)
        view = ItemsView(bot, interaction.user.id, 'search', (kind.value, query, page), 'Discover')
        await view.load()
        await interaction.followup.send(embed=view.render(), view=view, ephemeral=True)

    @bot.tree.command(name='library', description='Browse available movies, shows, albums and books')
    @app_commands.choices(kind=[app_commands.Choice(name=label, value=value) for label, value in [('Everything', 'all'), ('Movies', 'movie'), ('Series', 'tv'), ('Music', 'music'), ('Books', 'book')]])
    async def library(interaction: discord.Interaction, kind: str = 'all', query: str = ''):
        bot.guard(interaction)
        await interaction.response.defer(ephemeral=True)
        view = ItemsView(bot, interaction.user.id, 'library', (kind,), 'Available library', query=query)
        await view.load()
        await interaction.followup.send(embed=view.render(), view=view, ephemeral=True)

    @bot.tree.command(name='storage', description='See verified live storage space across configured media services')
    async def storage(interaction: discord.Interaction):
        bot.guard(interaction)
        await DashboardView(bot, interaction.user.id).storage(interaction)

    @bot.tree.command(name='notifications', description='Opt in/out of personal updates and choose your Jellyfin device link')
    async def notifications(interaction: discord.Interaction, enabled: bool = None):
        bot.guard(interaction)
        await interaction.response.defer(ephemeral=True)
        bot.service.account(interaction.user.id)
        if enabled is True:
            await bot.service.run(interaction.user.id, 'preferences', True)
        elif enabled is False:
            bot.service.mute_notifications(interaction.user.id)
        view = PreferencesView(bot, interaction.user.id)
        await interaction.followup.send(embed=view.render(), view=view, ephemeral=True)

    @bot.tree.command(name='watchlist', description='Browse and remove followed items for personal state-change notifications')
    async def watchlist(interaction: discord.Interaction, query: str = ''):
        bot.guard(interaction)
        await interaction.response.defer(ephemeral=True)
        view = ItemsView(bot, interaction.user.id, 'watches', (), 'My watchlist', query=query)
        await view.load()
        await interaction.followup.send(embed=view.render(), view=view, ephemeral=True)

    @bot.tree.command(name='deletions', description='View scheduled file deletions and undo them during the 24-hour delay')
    async def deletions(interaction: discord.Interaction, query: str = ''):
        bot.guard(interaction)
        await interaction.response.defer(ephemeral=True)
        view = ItemsView(bot, interaction.user.id, 'deletions', (), 'My file deletions · 24-hour undo window', query=query)
        await view.load()
        await interaction.followup.send(embed=view.render(), view=view, ephemeral=True)

    @bot.tree.command(name='status', description='Inspect the database health and last verified refresh (diagnostic only)')
    async def status(interaction: discord.Interaction):
        bot.guard(interaction)
        bot.service.account(interaction.user.id)
        meta = bot.service.store.meta()
        healthy = (not meta.get('error') and bool(meta.get('last_success')) and
                   time.time() - float(meta['last_success']) <= bot.config.sync_interval * 2)
        result = embed('Database health', '🟢 Last refresh succeeded' if healthy else '🔴 Not verified · data operations blocked until a successful live refresh')
        if meta.get('last_success'):
            result.add_field(name='Last complete refresh', value=f"<t:{int(float(meta['last_success']))}:F>")
        result.add_field(name='Background interval', value=f'{bot.config.sync_interval} seconds')
        result.add_field(name='Freshness policy', value='Every data-backed command and action refreshes first. Failed refreshes never fall back to cached data.', inline=False)
        result.add_field(name='Services', value=', '.join(['Seerr'] + [s.title() for s in bot.config.arr]))
        await interaction.response.send_message(embed=result, ephemeral=True)

    @bot.tree.command(name='seerr', description='Open your Seerr server')
    async def seerr(interaction: discord.Interaction):
        bot.guard(interaction)
        await interaction.response.defer(ephemeral=True)
        await bot.service.run(interaction.user.id, 'dashboard')
        view = discord.ui.View()
        view.add_item(discord.ui.Button(label='Open Seerr', url=bot.config.seerr_url))
        await interaction.followup.send('Your media requests, on the web.', view=view, ephemeral=True)


def main():
    load_dotenv()
    logging.basicConfig(level=logging.INFO, format='%(asctime)s %(levelname)s %(name)s: %(message)s')
    asyncio.run(run_application())


async def run_application():
    from media_bot.config import boolean, integer
    from media_bot.panel import ControlPanel
    panel = None
    bot = None
    bot_task = None
    if boolean('PANEL_ENABLED', True):
        port = integer('PANEL_PORT', 0, 0)
        if port > 65535:
            raise ValueError('PANEL_PORT must be between 0 and 65535')
        panel = ControlPanel(os.getenv('PANEL_HOST', '127.0.0.1'), port)
        await panel.start()
        address = '127.0.0.1' if panel.host in ('0.0.0.0', '::') else panel.host
        print(f'Local admin panel: http://{address}:{panel.port}\nOne-time access code: {panel.code}\n'
              'Keep this code/private port out of public proxies and shared logs.', flush=True)
    try:
        while True:
            try:
                config = Config.from_env()
                bot = SeerrBot(config)
            except (ValueError, OSError, sqlite3.Error) as exc:
                if not panel:
                    raise
                panel.runtime_error = ('Configuration/storage is not ready. Complete setup or correct '
                    'writable DATA_DIR and environment overrides. ' + type(exc).__name__)
                await panel.changed.wait()
                panel.changed.clear()
                continue
            if panel:
                panel.bot = bot
                panel.runtime_error = ''
            bot_task = asyncio.create_task(bot.start(config.discord_token))
            if not panel:
                await bot_task
                return
            change_task = asyncio.create_task(panel.changed.wait())
            try:
                done, _pending = await asyncio.wait((bot_task, change_task), return_when=asyncio.FIRST_COMPLETED)
                if bot_task in done:
                    error = bot_task.exception()
                    panel.runtime_error = 'Bot stopped' + (f' ({type(error).__name__}). Check credentials, intents, backend port and storage.' if error else '.')
                    log.warning('%s', panel.runtime_error)
                await bot.close()
                await asyncio.gather(bot_task, return_exceptions=True)
                panel.bot = None
                if not change_task.done():
                    await change_task
                panel.changed.clear()
            finally:
                change_task.cancel()
                await asyncio.gather(change_task, return_exceptions=True)
    finally:
        if bot:
            await bot.close()
        if bot_task:
            bot_task.cancel()
            await asyncio.gather(bot_task, return_exceptions=True)
        if panel:
            await panel.close()


if __name__ == '__main__':
    main()
