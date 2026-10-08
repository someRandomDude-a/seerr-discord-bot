"""Verified operator announcements and a bounded, private received-message inbox."""
import asyncio
import secrets
import time
import threading
from dataclasses import dataclass, field

import discord
from .security import RateLimiter, UserError


@dataclass
class SendPlan:
    actor: int | str
    content: str
    destinations: list
    expires: float
    skipped: list = field(default_factory=list)


class AdminMessaging:
    def __init__(self, bot):
        self.bot, self.config, self.store = bot, bot.config, bot.service.store
        self.plans = {}
        self.plan_lock = asyncio.Lock()
        self.plans_lock = threading.Lock()
        self.loop = asyncio.get_running_loop()
        self.limiter = RateLimiter(self.config.admin_send_limit, self.config.admin_send_window)
        self.wake = asyncio.Event()

    def is_admin(self, user_id):
        if user_id == 'local-panel':
            return True  # Internal actor only; authenticated local panel, never accepted from Discord.
        return self.bot.service.is_admin(user_id)

    def require_admin(self, user_id):
        if not self.is_admin(user_id):
            raise UserError('Operator access required.')

    def channel_for(self, guild, preferred=None):
        channel_id = self.config.announcement_channels.get(guild.id)
        channel = guild.get_channel_or_thread(preferred or channel_id) if preferred or channel_id else guild.system_channel
        if not isinstance(channel, (discord.TextChannel, discord.Thread)) or not guild.me:
            return None
        permissions = channel.permissions_for(guild.me)
        allowed = permissions.send_messages_in_threads if isinstance(channel, discord.Thread) else permissions.send_messages
        return channel if permissions.view_channel and allowed else None

    async def prepare(self, actor, content, guild_ids=(), user_id=None, all_users=False, channel_id=None):
        await self.bot.service.offload(self.require_admin, actor)
        if not isinstance(content, str) or not 1 <= len(content.strip()) <= 2000:
            raise UserError('Message must contain 1–2000 characters.')
        sensitive = [self.config.discord_token, self.config.seerr_key, self.config.client_secret, self.config.webhook_secret]
        sensitive.extend(c.key for c in self.config.arr.values())
        sensitive.extend(c.config.key for c in list(self.bot.service.arr.values()))
        if any(isinstance(value, str) and len(value) >= 8 and value in content for value in sensitive):
            raise UserError('The message appears to contain a configured secret. Remove it first.')
        self.limiter.check(actor)
        async with self.plan_lock:
            guilds, destinations, skipped = [], [], []
            for guild_id in set(guild_ids):
                guild = self.bot.get_guild(int(guild_id))
                if not guild:
                    raise UserError('A selected server is not available to this bot.')
                guilds.append(guild)
            if user_id is not None:
                if channel_id:
                    raise UserError('A channel override cannot be combined with a user DM.')
                if all_users or len(guilds) > 1:
                    raise UserError('Choose one user, or a server broadcast—not both.')
                if guilds:
                    try:
                        await guilds[0].fetch_member(int(user_id))
                    except discord.HTTPException:
                        raise UserError('That user could not be verified in the selected server.') from None
                user = self.bot.get_user(int(user_id)) or await self.bot.fetch_user(int(user_id))
                if user.bot:
                    raise UserError('Bot accounts cannot receive announcements.')
                destinations.append({'kind': 'user', 'id': str(user.id), 'label': str(user)})
            else:
                if not guilds:
                    raise UserError('Select a server or a user. No automatic global broadcast.')
                if channel_id and len(guilds) != 1:
                    raise UserError('A channel override requires one server.')
                for guild in guilds:
                    channel = self.channel_for(guild, int(channel_id) if channel_id else None)
                    if channel:
                        destinations.append({'kind': 'channel', 'id': str(channel.id), 'label': f'{guild.name} / #{channel.name}'})
                    else:
                        skipped.append(guild.name)
                if all_users:
                    if not self.config.members_intent:
                        raise UserError('All-member DMs require ENABLE_MEMBERS_INTENT and Server Members Intent in the Discord Developer Portal.')
                    users = set()
                    for guild in guilds:
                        async for member in guild.fetch_members(limit=None):
                            if not member.bot and member.id not in users:
                                users.add(member.id)
                                destinations.append({'kind': 'user', 'id': str(member.id), 'label': str(member)})
                                if len(destinations) > self.config.admin_max_recipients:
                                    raise UserError('Recipient limit exceeded. Select fewer servers or raise ADMIN_MAX_RECIPIENTS deliberately.')
            if not destinations:
                raise UserError('No sendable channels. Configure ADMIN_ANNOUNCEMENT_CHANNELS or a server system channel.')
            if len(destinations) > self.config.admin_max_recipients:
                raise UserError('Recipient limit exceeded.')
            token = secrets.token_urlsafe(24)
            with self.plans_lock:
                self.plans = {key: plan for key, plan in self.plans.items() if plan.expires > time.monotonic()}
                self.plans[token] = SendPlan(actor, content, destinations, time.monotonic() + 120, skipped)
            return {'plan': token, 'channels': sum(d['kind'] == 'channel' for d in destinations),
                    'users': sum(d['kind'] == 'user' for d in destinations), 'skipped': skipped,
                    'destinations': [d['label'] for d in destinations[:20]], 'message': content}

    def confirm(self, actor, token):
        self.require_admin(actor)
        if not isinstance(token, str):
            raise UserError('Invalid send preview.')
        with self.plans_lock:
            plan = self.plans.get(token)
            if not plan or plan.actor != actor or plan.expires <= time.monotonic():
                raise UserError('Send preview expired or belongs to another operator. Preview again.')
            self.plans.pop(token)
        with self.store.connect() as db:
            job = db.execute('INSERT INTO announcements(actor_id,content,created_at) VALUES (?,?,?)',
                             (str(actor), plan.content, time.time())).lastrowid
            db.executemany('INSERT INTO announcement_deliveries(announcement_id,kind,target_id,label) VALUES (?,?,?,?)',
                           [(job, d['kind'], d['id'], d['label']) for d in plan.destinations])
        self.loop.call_soon_threadsafe(self.wake.set)
        return job

    def state(self, actor):
        self.require_admin(actor)
        self.prune_inbox()
        guilds = []
        for guild in sorted(self.bot.guilds, key=lambda g: g.name.lower()):
            channel = self.channel_for(guild)
            guilds.append({'id': str(guild.id), 'name': guild.name, 'channel': channel.name if channel else None,
                           'members': guild.member_count})
        with self.store.connect() as db:
            threads = [dict(r) for r in db.execute('''SELECT CASE WHEN guild_id IS NULL THEN 'dm' ELSE 'guild' END AS kind,
                CASE WHEN guild_id IS NULL THEN author_id ELSE guild_id END AS id,
                CASE WHEN guild_id IS NULL THEN author_name ELSE guild_name END AS name,
                COUNT(*) AS count,MAX(created_at) AS latest FROM inbox GROUP BY kind,
                CASE WHEN guild_id IS NULL THEN author_id ELSE guild_id END ORDER BY latest DESC''')]
            jobs = [dict(r) for r in db.execute('''SELECT a.id,a.created_at,
                SUM(d.status='sent') AS sent,SUM(d.status='failed') AS failed,
                SUM(d.status='uncertain') AS uncertain,SUM(d.status IN ('pending','sending')) AS pending
                FROM announcements a JOIN announcement_deliveries d ON d.announcement_id=a.id
                GROUP BY a.id ORDER BY a.id DESC LIMIT 10''')]
        return {'guilds': guilds, 'threads': threads, 'jobs': jobs, 'all_users_enabled': self.config.members_intent,
                'retention_days': self.config.inbox_retention_days, 'max_messages': self.config.inbox_max_messages}

    def inbox(self, actor, *, kind='all', guild_id=None, user_id=None, channel_id=None, query='', page=1, order='newest', page_size=50):
        self.require_admin(actor)
        self.prune_inbox()
        if (kind not in ('all', 'dm', 'guild') or order not in ('newest', 'oldest') or
            not isinstance(query, str) or len(query) > 100 or isinstance(page, bool) or
            not isinstance(page, int) or not 1 <= page <= 10000 or
            isinstance(page_size, bool) or not isinstance(page_size, int) or not 1 <= page_size <= 50):
            raise UserError('Invalid inbox filter.')
        clauses, params = [], []
        if kind != 'all':
            clauses.append('guild_id IS NULL' if kind == 'dm' else 'guild_id IS NOT NULL')
        for key, value in [('guild_id', guild_id), ('author_id', user_id), ('channel_id', channel_id)]:
            if value is not None:
                if not isinstance(value, str) or not value.isascii() or not value.isdigit() or not 0 < int(value) < 2 ** 64:
                    raise UserError('Invalid inbox ID filter.')
                clauses.append(f'{key}=?')
                params.append(str(value))
        if query:
            clauses.append('(INSTR(LOWER(content),LOWER(?))>0 OR INSTR(LOWER(author_name),LOWER(?))>0)')
            params.extend([query, query])
        where = ' WHERE ' + ' AND '.join(clauses) if clauses else ''
        with self.store.connect() as db:
            total = db.execute('SELECT COUNT(*) FROM inbox' + where, params).fetchone()[0]
            direction = 'DESC' if order == 'newest' else 'ASC'
            messages = [dict(r) for r in db.execute('SELECT * FROM inbox' + where + f' ORDER BY created_at {direction},id {direction} LIMIT ? OFFSET ?', params + [page_size, (page - 1) * page_size])]
            channels = [dict(r) for r in db.execute('SELECT DISTINCT channel_id,channel_name,guild_id,guild_name FROM inbox WHERE guild_id IS NOT NULL ORDER BY guild_name,channel_name')]
            users = [dict(r) for r in db.execute('SELECT author_id,MAX(author_name) AS author_name FROM inbox GROUP BY author_id ORDER BY author_name')]
        return {'messages': messages, 'total': total, 'page': page, 'channels': channels, 'users': users}

    def deliveries(self, actor, job, page=1):
        self.require_admin(actor)
        with self.store.connect() as db:
            announcement = db.execute('SELECT * FROM announcements WHERE id=?', (job,)).fetchone()
            if not announcement:
                raise UserError('Announcement not found.')
            total = db.execute('SELECT COUNT(*) FROM announcement_deliveries WHERE announcement_id=?', (job,)).fetchone()[0]
            rows = [dict(r) for r in db.execute('SELECT * FROM announcement_deliveries WHERE announcement_id=? ORDER BY id LIMIT 5 OFFSET ?', (job, (page - 1) * 5))]
        return {'announcement': dict(announcement), 'rows': rows, 'total': total}

    def prune_inbox(self):
        with self.store.connect() as db:
            db.execute('DELETE FROM inbox WHERE created_at<?', (time.time() - self.config.inbox_retention_days * 86400,))
            db.execute('DELETE FROM inbox WHERE id NOT IN (SELECT id FROM inbox ORDER BY id DESC LIMIT ?)', (self.config.inbox_max_messages,))

    async def receive(self, message):
        if not (self.config.admin_ids or self.config.seerr_admins) or not self.bot.user or message.author.id == self.bot.user.id:
            return
        direct = message.guild is None
        mentioned = any(user.id == self.bot.user.id for user in message.mentions)
        referenced = getattr(message.reference, 'resolved', None)
        reply = isinstance(referenced, discord.Message) and referenced.author.id == self.bot.user.id
        configured = message.channel.id in self.config.inbox_channel_ids
        if not (direct or mentioned or reply or configured):
            return
        content = message.content or ''
        with self.store.connect() as db:
            db.execute('''INSERT OR IGNORE INTO inbox(message_id,author_id,author_name,guild_id,guild_name,
                channel_id,channel_name,content,attachment_count,created_at) VALUES (?,?,?,?,?,?,?,?,?,?)''',
                (str(message.id), str(message.author.id), str(message.author), str(message.guild.id) if message.guild else None,
                 message.guild.name if message.guild else None, str(message.channel.id), getattr(message.channel, 'name', 'DM'),
                 content[:4000], len(message.attachments), message.created_at.timestamp()))
        self.prune_inbox()

    async def deliver(self):
        # Claims are durable before network sends; interrupted/ambiguous writes never auto-replay.
        with self.store.connect() as db:
            db.execute("UPDATE announcement_deliveries SET status='uncertain',error='Interrupted send; verify Discord before resending' WHERE status='sending'")
        await self.bot.wait_until_ready()
        while not self.bot.is_closed():
            self.wake.clear()
            with self.store.connect() as db:
                rows = [dict(r) for r in db.execute('''SELECT d.*,a.content,a.actor_id FROM announcement_deliveries d
                    JOIN announcements a ON a.id=d.announcement_id WHERE d.status='pending' ORDER BY d.id LIMIT 50''')]
            for row in rows:
                try:
                    allowed = await self.bot.service.offload(self.is_admin, row['actor_id'])
                except Exception:
                    await asyncio.sleep(30)  # No send occurred: leave pending until authority can be verified.
                    break
                with self.store.connect() as db:
                    db.execute("UPDATE announcement_deliveries SET status='sending' WHERE id=?", (row['id'],))
                try:
                    if not allowed:
                        raise UserError('Operator access was revoked')
                    if row['kind'] == 'user':
                        target = self.bot.get_user(int(row['target_id'])) or await self.bot.fetch_user(int(row['target_id']))
                    else:
                        target = self.bot.get_channel(int(row['target_id'])) or await self.bot.fetch_channel(int(row['target_id']))
                    message = await target.send(row['content'], allowed_mentions=discord.AllowedMentions.none())
                    status, error, message_id = 'sent', None, str(message.id)
                except (discord.Forbidden, discord.NotFound):
                    status, error, message_id = 'failed', 'Recipient unavailable or messages not permitted', None
                except UserError:
                    status, error, message_id = 'failed', 'Operator access revoked', None
                except asyncio.CancelledError:
                    raise
                except Exception:
                    status, error, message_id = 'uncertain', 'Send not confirmed; verify Discord before resending', None
                with self.store.connect() as db:
                    db.execute('UPDATE announcement_deliveries SET status=?,error=?,message_id=? WHERE id=?', (status, error, message_id, row['id']))
                await asyncio.sleep(self.config.admin_send_interval)
            if rows:
                continue
            try:
                await asyncio.wait_for(self.wake.wait(), timeout=60)
            except asyncio.TimeoutError:
                pass
