"""Verified operator announcements and a bounded, private received-message inbox."""
import asyncio
import secrets
import time
import threading
import json
import hashlib
import io
import re
from dataclasses import dataclass, field

import discord
from .security import RateLimiter, UserError
from .chat import ChatFiles, metadata, public_message


@dataclass
class SendPlan:
    actor: int | str
    content: str
    destinations: list
    expires: float
    skipped: list = field(default_factory=list)
    mode: str = 'announcement'
    uploads: list = field(default_factory=list)
    request_id: str | None = None
    fingerprint: str | None = None


class AdminMessaging:
    def __init__(self, bot):
        self.bot, self.config, self.store = bot, bot.config, bot.service.store
        self.plans = {}
        self.plan_lock = asyncio.Lock()
        self.plans_lock = threading.Lock()
        self.loop = asyncio.get_running_loop()
        self.limiter = RateLimiter(self.config.admin_send_limit, self.config.admin_send_window)
        self.chat_limiter = RateLimiter(30, 60)
        self.wake = asyncio.Event()
        self.files = ChatFiles(self.store)
        self.subscribers = set()
        self.revision = 0

    def publish(self):
        self.loop.call_soon_threadsafe(self._publish)

    def _publish(self):
        self.revision += 1
        for queue in list(self.subscribers):
            if queue.full():
                queue.get_nowait()
            queue.put_nowait(self.revision)

    def subscribe(self):
        if len(self.subscribers) >= 16:
            raise UserError('Too many live messaging connections.')
        queue = asyncio.Queue(maxsize=1)
        self.subscribers.add(queue)
        queue.put_nowait(self.revision)
        return queue

    def is_admin(self, user_id):
        if user_id == 'local-panel':
            return True  # Internal actor only; authenticated local panel, never accepted from Discord.
        return self.bot.service.is_admin(user_id)

    def require_admin(self, user_id):
        if user_id != 'local-panel':
            self.bot.service.verify_identity(user_id)
        if not self.is_admin(user_id):
            raise UserError('Operator access required.')

    def channel_for(self, guild, preferred=None):
        channel_id = self.config.announcement_channels.get(guild.id)
        channel = guild.get_channel_or_thread(preferred or channel_id) if preferred or channel_id else guild.system_channel
        if not isinstance(channel, (discord.TextChannel, discord.Thread)) or not guild.me:
            return None
        permissions = channel.permissions_for(guild.me)
        if isinstance(channel, discord.Thread) and (channel.archived or channel.locked):
            return None
        allowed = permissions.send_messages_in_threads if isinstance(channel, discord.Thread) else permissions.send_messages
        return channel if permissions.view_channel and allowed else None

    async def prepare(self, actor, content, guild_ids=(), user_id=None, all_users=False, channel_id=None,
                      *, mode='announcement', uploads=None, request_id=None, fingerprint=None):
        await self.bot.service.offload(self.require_admin, actor)
        uploads = [] if uploads is None else uploads
        if mode not in ('announcement', 'normal') or not isinstance(content, str) or len(content) > 2000 or not (content.strip() or uploads):
            raise UserError('Enter a message (up to 2000 characters) or attach a file.')
        file_rows = await self.bot.service.offload(self.files.resolve, actor, uploads)
        if mode == 'normal' and (all_users or len(guild_ids) > 1 or user_id is None and (len(guild_ids) != 1 or not channel_id)):
            raise UserError('Normal chat requires one DM or an explicitly selected server channel. Broadcasts use announcements.')
        sensitive = [self.config.discord_token, self.config.seerr_key, self.config.client_secret, self.config.webhook_secret]
        sensitive.extend(c.key for c in self.config.arr.values())
        sensitive.extend(c.config.key for c in list(self.bot.service.arr.values()))
        if any(isinstance(value, str) and len(value) >= 8 and value in content for value in sensitive):
            raise UserError('The message appears to contain a configured secret. Remove it first.')
        (self.chat_limiter if mode == 'normal' else self.limiter).check(actor)
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
                destinations.append({'kind': 'user', 'id': str(user.id), 'label': str(user), 'peer_id': str(user.id), 'peer_name': str(user)})
            else:
                if not guilds:
                    raise UserError('Select a server or a user. No automatic global broadcast.')
                if channel_id and len(guilds) != 1:
                    raise UserError('A channel override requires one server.')
                for guild in guilds:
                    channel = self.channel_for(guild, int(channel_id) if channel_id else None)
                    if channel:
                        if file_rows and not channel.permissions_for(guild.me).attach_files:
                            raise UserError('The bot lacks Attach Files permission in a selected channel.')
                        destinations.append({'kind': 'channel', 'id': str(channel.id), 'label': f'{guild.name} / #{channel.name}',
                            'guild_id': str(guild.id), 'guild_name': guild.name, 'channel_name': channel.name})
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
                                destinations.append({'kind': 'user', 'id': str(member.id), 'label': str(member), 'peer_id': str(member.id), 'peer_name': str(member)})
                                if len(destinations) > self.config.admin_max_recipients:
                                    raise UserError('Recipient limit exceeded. Select fewer servers or raise ADMIN_MAX_RECIPIENTS deliberately.')
            if not destinations:
                raise UserError('No sendable channels. Configure ADMIN_ANNOUNCEMENT_CHANNELS or a server system channel.')
            if len(destinations) > self.config.admin_max_recipients:
                raise UserError('Recipient limit exceeded.')
            token = secrets.token_urlsafe(24)
            with self.plans_lock:
                self.plans = {key: plan for key, plan in self.plans.items() if plan.expires > time.monotonic()}
                self.plans[token] = SendPlan(actor, content, destinations, time.monotonic() + 120, skipped, mode, uploads, request_id, fingerprint)
            return {'plan': token, 'channels': sum(d['kind'] == 'channel' for d in destinations),
                    'users': sum(d['kind'] == 'user' for d in destinations), 'skipped': skipped,
                    'destinations': [d['label'] for d in destinations[:20]], 'message': content, 'mode': mode,
                    'attachments': [{'id': row['id'], 'filename': row['filename'], 'size': len(row['body'])} for row in file_rows]}

    async def chat(self, actor, content, *, user_id=None, guild_id=None, channel_id=None, uploads=None, request_id=None):
        await self.bot.service.offload(self.require_admin, actor)
        if not isinstance(request_id, str) or not re.fullmatch(r'[A-Za-z0-9_-]{16,80}', request_id):
            raise UserError('A unique chat send ID is required.')
        fingerprint = hashlib.sha256(json.dumps([content, user_id, guild_id, channel_id, uploads or []], sort_keys=True).encode()).hexdigest()
        with self.store.connect() as db:
            previous = db.execute('SELECT id,fingerprint FROM announcements WHERE actor_id=? AND request_id=?', (str(actor), request_id)).fetchone()
        if previous:
            if previous['fingerprint'] != fingerprint:
                raise UserError('This send ID belongs to a different message. Review the previous result before sending again.')
            return previous['id']
        plan = await self.prepare(actor, content, [guild_id] if guild_id else [], user_id=user_id,
            channel_id=channel_id, mode='normal', uploads=uploads, request_id=request_id, fingerprint=fingerprint)
        return await self.bot.service.offload(self.confirm, actor, plan['plan'])

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
            db.execute('BEGIN IMMEDIATE')
            if plan.request_id:
                old = db.execute('SELECT id,fingerprint FROM announcements WHERE actor_id=? AND request_id=?', (str(actor), plan.request_id)).fetchone()
                if old:
                    if old['fingerprint'] != plan.fingerprint:
                        raise UserError('Conflicting chat send ID.')
                    return old['id']
            file_rows = self.files.resolve(actor, plan.uploads)
            job = db.execute('INSERT INTO announcements(actor_id,content,created_at,mode,uploads,request_id,fingerprint) VALUES (?,?,?,?,?,?,?)',
                (str(actor), plan.content, time.time(), plan.mode, json.dumps(plan.uploads), plan.request_id, plan.fingerprint)).lastrowid
            for row in file_rows:
                db.execute('UPDATE admin_files SET announcement_id=?,expires=? WHERE id=?', (job, time.time() + self.config.inbox_retention_days * 86400, row['id']))
            for dest in plan.destinations:
                delivery = db.execute('INSERT INTO announcement_deliveries(announcement_id,kind,target_id,label) VALUES (?,?,?,?)',
                    (job, dest['kind'], dest['id'], dest['label'])).lastrowid
                rich = {'mode': plan.mode, 'bot': True, 'job': job,
                    'attachments': [{'upload': row['id'], 'filename': row['filename'], 'size': len(row['body']), 'image': row['content_type'].startswith('image/')} for row in file_rows]}
                db.execute('''INSERT INTO inbox(message_id,author_id,author_name,guild_id,guild_name,channel_id,channel_name,
                    content,attachment_count,created_at,direction,peer_id,peer_name,rich,delivery_id,status)
                    VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)''',
                    (f'send-{delivery}', str(self.bot.user.id), 'Media', dest.get('guild_id'), dest.get('guild_name'),
                     dest['id'] if dest['kind'] == 'channel' else '', dest.get('channel_name', 'DM'), plan.content,
                     len(file_rows), time.time(), 'out', dest.get('peer_id', dest['id'] if dest['kind'] == 'user' else None),
                     dest.get('peer_name', dest['label'] if dest['kind'] == 'user' else None), json.dumps(rich), delivery, 'pending'))
        self.loop.call_soon_threadsafe(self.wake.set)
        self.publish()
        return job

    def state(self, actor):
        self.require_admin(actor)
        self.prune_inbox()
        guilds = []
        for guild in sorted(self.bot.guilds, key=lambda g: g.name.lower()):
            channel = self.channel_for(guild)
            guilds.append({'id': str(guild.id), 'name': guild.name, 'channel': channel.name if channel else None,
                           'default_channel_id': str(channel.id) if channel else None, 'members': guild.member_count,
                           'channels': [{'id': str(c.id), 'name': c.name, 'category': getattr(getattr(c, 'category', None), 'name', None),
                               'thread': isinstance(c, discord.Thread), 'sendable': self.channel_for(guild, c.id) is not None}
                               for c in list(getattr(guild, 'text_channels', [])) + list(getattr(guild, 'threads', []))
                               if guild.me and c.permissions_for(guild.me).view_channel]})
        with self.store.connect() as db:
            threads = [dict(r) for r in db.execute('''SELECT CASE WHEN guild_id IS NULL THEN 'dm' ELSE 'guild' END AS kind,
                CASE WHEN guild_id IS NULL THEN COALESCE(peer_id,author_id) ELSE guild_id END AS id,
                CASE WHEN guild_id IS NULL THEN COALESCE(peer_name,author_name) ELSE guild_name END AS name,
                COUNT(*) AS count,MAX(created_at) AS latest FROM inbox GROUP BY kind,
                CASE WHEN guild_id IS NULL THEN COALESCE(peer_id,author_id) ELSE guild_id END ORDER BY latest DESC''')]
            jobs = [dict(r) for r in db.execute('''SELECT a.id,a.created_at,
                SUM(d.status='sent') AS sent,SUM(d.status='failed') AS failed,
                SUM(d.status='uncertain') AS uncertain,SUM(d.status IN ('pending','sending')) AS pending
                FROM announcements a JOIN announcement_deliveries d ON d.announcement_id=a.id
                GROUP BY a.id ORDER BY a.id DESC LIMIT 10''')]
        return {'guilds': guilds, 'threads': threads, 'jobs': jobs, 'revision': self.revision, 'all_users_enabled': self.config.members_intent,
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
        for key, value in [('guild_id', guild_id), ('user', user_id), ('channel_id', channel_id)]:
            if value is not None:
                if not isinstance(value, str) or not value.isascii() or not value.isdigit() or not 0 < int(value) < 2 ** 64:
                    raise UserError('Invalid inbox ID filter.')
                clauses.append("(CASE WHEN guild_id IS NULL THEN COALESCE(peer_id,author_id) ELSE author_id END)=?" if key == 'user' else f'{key}=?')
                params.append(str(value))
        if query:
            clauses.append('(INSTR(LOWER(content),LOWER(?))>0 OR INSTR(LOWER(author_name),LOWER(?))>0)')
            params.extend([query, query])
        where = ' WHERE ' + ' AND '.join(clauses) if clauses else ''
        with self.store.connect() as db:
            total = db.execute('SELECT COUNT(*) FROM inbox' + where, params).fetchone()[0]
            direction = 'DESC' if order == 'newest' else 'ASC'
            messages = [public_message(r) for r in db.execute('SELECT * FROM inbox' + where + f' ORDER BY created_at {direction},id {direction} LIMIT ? OFFSET ?', params + [page_size, (page - 1) * page_size])]
            channels = [dict(r) for r in db.execute('SELECT DISTINCT channel_id,channel_name,guild_id,guild_name FROM inbox WHERE guild_id IS NOT NULL ORDER BY guild_name,channel_name')]
            users = [dict(r) for r in db.execute('SELECT author_id,MAX(author_name) AS author_name FROM inbox GROUP BY author_id ORDER BY author_name')]
        return {'messages': messages, 'total': total, 'page': page, 'channels': channels, 'users': users}

    def history(self, actor, page=1):
        self.require_admin(actor)
        if type(page) is not int or not 1 <= page <= 10000:
            raise UserError('Invalid history page.')
        with self.store.connect() as db:
            total = db.execute("SELECT COUNT(*) FROM announcements WHERE mode='announcement'").fetchone()[0]
            jobs = []
            for row in db.execute("SELECT * FROM announcements WHERE mode='announcement' ORDER BY id DESC LIMIT 25 OFFSET ?", ((page - 1) * 25,)):
                job = dict(row)
                job['deliveries'] = [dict(d) for d in db.execute('SELECT label,status,error FROM announcement_deliveries WHERE announcement_id=? ORDER BY id LIMIT 20', (row['id'],))]
                job['counts'] = dict(db.execute('''SELECT COUNT(*) AS total,SUM(status='sent') AS sent,
                    SUM(status='failed') AS failed,SUM(status='uncertain') AS uncertain,
                    SUM(status IN ('pending','sending')) AS pending FROM announcement_deliveries WHERE announcement_id=?''', (row['id'],)).fetchone())
                job['attachments'] = [{'upload': f['id'], 'filename': f['filename'], 'size': f['size'], 'image': f['content_type'].startswith('image/')}
                    for f in db.execute('SELECT id,filename,LENGTH(body) AS size,content_type FROM admin_files WHERE announcement_id=? AND expires>?', (row['id'], time.time()))]
                jobs.append(job)
        return {'jobs': jobs, 'page': page, 'total': total}

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
        self.files.prune()
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
        if not direct and not configured:
            with self.store.connect() as db:
                configured = db.execute('''SELECT 1 FROM inbox WHERE guild_id=? AND channel_id=? AND direction='out'
                    AND created_at>? AND json_extract(rich,'$.mode')='normal' LIMIT 1''',
                    (str(message.guild.id), str(message.channel.id), time.time() - self.config.inbox_retention_days * 86400)).fetchone() is not None
        if not (direct or mentioned or reply or configured):
            return
        content = message.content or ''
        with self.store.connect() as db:
            db.execute('''INSERT OR IGNORE INTO inbox(message_id,author_id,author_name,guild_id,guild_name,
                channel_id,channel_name,content,attachment_count,created_at) VALUES (?,?,?,?,?,?,?,?,?,?)''',
                 (str(message.id), str(message.author.id), str(getattr(message.author, 'display_name', None) or message.author), str(message.guild.id) if message.guild else None,
                 message.guild.name if message.guild else None, str(message.channel.id), getattr(message.channel, 'name', 'DM'),
                  content[:4000], len(message.attachments), message.created_at.timestamp()))
            db.execute('UPDATE inbox SET peer_id=?,peer_name=?,rich=? WHERE message_id=?',
                (str(message.author.id) if direct else None, str(getattr(message.author, 'display_name', None) or message.author) if direct else None,
                 json.dumps(metadata(message)), str(message.id)))
        self.prune_inbox()
        self.publish()

    async def edited(self, message, *, mark_edited=True):
        with self.store.connect() as db:
            previous = db.execute('SELECT rich,edited_at FROM inbox WHERE message_id=? AND channel_id=? AND deleted=0', (str(message.id), str(message.channel.id))).fetchone()
            if not previous:
                return
            rich = {**json.loads(previous['rich']), **metadata(message)}
            db.execute('UPDATE inbox SET content=?,rich=?,attachment_count=?,edited_at=? WHERE message_id=? AND channel_id=?',
                ((message.content or '')[:4000], json.dumps(rich), len(message.attachments), time.time() if mark_edited else previous['edited_at'], str(message.id), str(message.channel.id)))
        self.publish()

    def deleted(self, message_id, channel_id):
        with self.store.connect() as db:
            db.execute("UPDATE inbox SET content='',rich='{}',attachment_count=0,deleted=1 WHERE message_id=? AND channel_id=?", (str(message_id), str(channel_id)))
        self.publish()

    async def deliver(self):
        # Claims are durable before network sends; interrupted/ambiguous writes never auto-replay.
        with self.store.connect() as db:
            db.execute("UPDATE announcement_deliveries SET status='uncertain',error='Interrupted send; verify Discord before resending' WHERE status='sending'")
            db.execute("UPDATE inbox SET status='uncertain' WHERE delivery_id IN (SELECT id FROM announcement_deliveries WHERE status='uncertain')")
        self.publish()
        await self.bot.wait_until_ready()
        while not self.bot.is_closed():
            self.wake.clear()
            with self.store.connect() as db:
                rows = [dict(r) for r in db.execute('''SELECT d.*,a.content,a.actor_id,a.uploads FROM announcement_deliveries d
                    JOIN announcements a ON a.id=d.announcement_id WHERE d.status='pending' ORDER BY d.id LIMIT 50''')]
            for row in rows:
                try:
                    allowed = await self.bot.service.offload(self.is_admin, row['actor_id'])
                except Exception:
                    await asyncio.sleep(30)  # No send occurred: leave pending until authority can be verified.
                    break
                with self.store.connect() as db:
                    db.execute("UPDATE announcement_deliveries SET status='sending' WHERE id=?", (row['id'],))
                    db.execute("UPDATE inbox SET status='sending' WHERE delivery_id=?", (row['id'],))
                self.publish()
                try:
                    if not allowed:
                        raise UserError('Operator access was revoked')
                    if row['kind'] == 'user':
                        target = self.bot.get_user(int(row['target_id'])) or await self.bot.fetch_user(int(row['target_id']))
                    else:
                        target = self.bot.get_channel(int(row['target_id'])) or await self.bot.fetch_channel(int(row['target_id']))
                    uploads = self.files.resolve(row['actor_id'], json.loads(row['uploads']), claimed=True)
                    kwargs = {'allowed_mentions': discord.AllowedMentions.none(), 'suppress_embeds': True}
                    if uploads:
                        kwargs['files'] = [discord.File(io.BytesIO(f['body']), filename=f['filename']) for f in uploads]
                    try:
                        message = await target.send(row['content'] or None, **kwargs)
                    finally:
                        for file in kwargs.get('files', []):
                            file.close()
                    status, error, message_id = 'sent', None, str(message.id)
                except (discord.Forbidden, discord.NotFound):
                    status, error, message_id = 'failed', 'Recipient unavailable or messages not permitted', None
                except UserError:
                    status, error, message_id = 'failed', 'Operator access revoked or attachment unavailable', None
                except asyncio.CancelledError:
                    raise
                except Exception:
                    status, error, message_id = 'uncertain', 'Send not confirmed; verify Discord before resending', None
                with self.store.connect() as db:
                    db.execute('UPDATE announcement_deliveries SET status=?,error=?,message_id=? WHERE id=?', (status, error, message_id, row['id']))
                    if message_id:
                        db.execute('UPDATE inbox SET message_id=?,channel_id=? WHERE delivery_id=?',
                            (message_id, str(getattr(getattr(message, 'channel', None), 'id', row['target_id'] if row['kind'] == 'channel' else '')), row['id']))
                        if hasattr(message, 'author') and hasattr(message, 'attachments'):
                            old = db.execute('SELECT rich FROM inbox WHERE delivery_id=?', (row['id'],)).fetchone()
                            if old:
                                rich = {**json.loads(old['rich']), **metadata(message)}
                                db.execute('UPDATE inbox SET rich=? WHERE delivery_id=?', (json.dumps(rich), row['id']))
                    db.execute('UPDATE inbox SET status=? WHERE delivery_id=?', (status, row['id']))
                self.publish()
                await asyncio.sleep(self.config.admin_send_interval)
            if rows:
                continue
            try:
                await asyncio.wait_for(self.wake.wait(), timeout=60)
            except asyncio.TimeoutError:
                pass
