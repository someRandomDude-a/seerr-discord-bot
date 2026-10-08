import asyncio
import tempfile
import time
import unittest
from datetime import datetime, timezone
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock, MagicMock

import discord

from bot import SeerrBot
from media_bot.admin import AdminMessaging
from media_bot.activity import snowflake
from media_bot.config import Config
from media_bot.security import UserError
from media_bot.store import Store
from media_bot.service import MediaService


class AdminTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.config = Config('http://seerr', 'private-seerr-key', 'private-discord-token',
            str(Path(self.temp.name) / 'cache.db'), admin_ids=frozenset([42, 43]),
            admin_send_interval=0, admin_send_limit=100)
        self.store = Store(self.config.database)
        self.channel = MagicMock(spec=discord.TextChannel)
        self.channel.id, self.channel.name = 111, 'announcements'
        self.channel.permissions_for.return_value = SimpleNamespace(view_channel=True, send_messages=True)
        self.channel.send = AsyncMock(return_value=SimpleNamespace(id=999))
        self.guild = MagicMock()
        self.guild.id, self.guild.name, self.guild.member_count = 555, 'Cinema', 3
        self.guild.system_channel = self.channel
        self.guild.get_channel_or_thread.return_value = self.channel
        self.user = MagicMock()
        self.user.id, self.user.bot = 7, False
        self.user.send = AsyncMock(return_value=SimpleNamespace(id=999))
        self.bot = MagicMock()
        self.bot.config, self.bot.service.store = self.config, self.store
        self.bot.service.is_admin.side_effect = lambda uid: int(uid) in self.config.admin_ids
        self.bot.service.offload = MediaService.offload
        self.bot.service.arr = {}
        self.bot.guilds, self.bot.user = [self.guild], SimpleNamespace(id=123)
        self.bot.get_guild.side_effect = lambda uid: self.guild if uid == 555 else None
        self.bot.get_user.return_value = self.user
        self.bot.get_channel.return_value = self.channel
        self.bot.wait_until_ready = AsyncMock()
        self.admin = AdminMessaging(self.bot)

    async def asyncTearDown(self):
        self.temp.cleanup()

    def message(self, uid=7, guild=None, channel=None, content='Hello', mid=100, mentions=()):
        return SimpleNamespace(id=mid, author=SimpleNamespace(id=uid, bot=False), guild=guild,
            channel=channel or SimpleNamespace(id=222, name='DM'), content=content, mentions=mentions,
            reference=None, attachments=[], created_at=datetime.now(timezone.utc))

    async def test_only_explicit_operators_can_read_or_send(self):
        for action in (lambda: self.admin.state(7), lambda: self.admin.inbox(7), lambda: self.admin.confirm(7, 'token')):
            with self.assertRaises(UserError):
                action()
        with self.assertRaises(UserError):
            await self.admin.prepare(7, 'Hello', [555])
        self.channel.send.assert_not_awaited()

    async def test_no_implicit_global_broadcast_or_secret_leak(self):
        with self.assertRaises(UserError):
            await self.admin.prepare(42, 'Hello')
        with self.assertRaises(UserError):
            await self.admin.prepare(42, 'Token: private-discord-token', [555])

    async def test_preview_is_actor_bound_one_use_and_durable(self):
        plan = await self.admin.prepare(42, '@everyone Hello', [555])
        self.channel.send.assert_not_awaited()
        with self.assertRaises(UserError):
            self.admin.confirm(43, plan['plan'])
        job = self.admin.confirm(42, plan['plan'])
        with self.assertRaises(UserError):
            self.admin.confirm(42, plan['plan'])
        with self.store.connect() as db:
            row = db.execute('SELECT * FROM announcement_deliveries WHERE announcement_id=?', (job,)).fetchone()
        self.assertEqual(row['target_id'], '111')
        self.assertEqual(row['status'], 'pending')
        self.bot.is_closed.side_effect = [False, True]
        await self.admin.deliver()
        self.assertEqual(self.channel.send.await_args.args, ('@everyone Hello',))
        self.assertEqual(self.channel.send.await_args.kwargs['allowed_mentions'].to_dict()['parse'], [])
        self.assertTrue(self.channel.send.await_args.kwargs['suppress_embeds'])
        self.assertEqual(self.admin.state(42)['jobs'][0]['sent'], 1)

    async def test_expired_and_malformed_previews_rejected(self):
        plan = await self.admin.prepare(42, 'Hello', [555])
        self.admin.plans[plan['plan']].expires = 0
        for token in (plan['plan'], [], None):
            with self.assertRaises(UserError):
                self.admin.confirm(42, token)

    async def test_interrupted_send_not_replayed(self):
        plan = await self.admin.prepare(42, 'Hello', [555])
        self.admin.confirm(42, plan['plan'])
        with self.store.connect() as db:
            db.execute("UPDATE announcement_deliveries SET status='sending'")
        self.bot.is_closed.return_value = True
        await self.admin.deliver()
        self.channel.send.assert_not_awaited()
        self.assertEqual(self.admin.state(42)['jobs'][0]['uncertain'], 1)

    async def test_ambiguous_send_not_retried(self):
        self.channel.send.side_effect = TimeoutError()
        plan = await self.admin.prepare(42, 'Hello', [555])
        self.admin.confirm(42, plan['plan'])
        self.bot.is_closed.side_effect = [False, True]
        await self.admin.deliver()
        self.channel.send.assert_awaited_once()
        self.assertEqual(self.admin.state(42)['jobs'][0]['uncertain'], 1)

    async def test_revoked_operator_pending_sends_blocked(self):
        plan = await self.admin.prepare(42, 'Hello', [555])
        self.admin.confirm(42, plan['plan'])
        self.config.admin_ids = frozenset([43])
        self.bot.is_closed.side_effect = [False, True]
        await self.admin.deliver()
        self.channel.send.assert_not_awaited()
        self.assertEqual(self.admin.state(43)['jobs'][0]['failed'], 1)

    async def test_configured_and_override_channels(self):
        self.config.announcement_channels = {555: 111}
        await self.admin.prepare(42, 'Hello', [555])
        self.guild.get_channel_or_thread.assert_called_with(111)
        await self.admin.prepare(42, 'Hello', [555], channel_id=222)
        self.guild.get_channel_or_thread.assert_called_with(222)
        self.channel.permissions_for.return_value.send_messages = False
        with self.assertRaises(UserError):
            await self.admin.prepare(42, 'Hello', [555])

    async def test_all_members_requires_intent_and_deduplicates(self):
        with self.assertRaises(UserError):
            await self.admin.prepare(42, 'Hello', [555], all_users=True)
        self.config.members_intent = True
        async def members(*, limit):
            for member in (self.user, self.user, SimpleNamespace(id=9, bot=True)):
                yield member
        self.guild.fetch_members = members
        plan = await self.admin.prepare(42, 'Hello', [555], all_users=True)
        self.assertEqual((plan['channels'], plan['users']), (1, 1))
        self.config.admin_max_recipients = 1
        with self.assertRaises(UserError):
            await self.admin.prepare(42, 'Hello', [555], all_users=True)

    async def test_receive_dms_mentions_not_unrelated_messages(self):
        await self.admin.receive(self.message(content='A DM'))
        await self.admin.receive(self.message(guild=self.guild, channel=self.channel, mid=101, content='Ping', mentions=[self.bot.user]))
        await self.admin.receive(self.message(guild=self.guild, channel=self.channel, mid=102, content='Unrelated'))
        await self.admin.receive(self.message(uid=123, mid=103))
        self.assertEqual(self.admin.inbox(42)['total'], 2)
        threads = self.admin.state(42)['threads']
        self.assertEqual({(t['kind'], t['id']) for t in threads}, {('dm', '7'), ('guild', '555')})

    async def test_reply_and_configured_channel_capture(self):
        message = self.message(guild=self.guild, channel=self.channel)
        referenced = MagicMock(spec=discord.Message)
        referenced.author.id = 123
        message.reference = SimpleNamespace(resolved=referenced)
        await self.admin.receive(message)
        self.config.inbox_channel_ids = frozenset([111])
        await self.admin.receive(self.message(guild=self.guild, channel=self.channel, mid=101))
        self.assertEqual(self.admin.inbox(42)['total'], 2)

    async def test_filters_search_order_and_pagination(self):
        for i in range(55):
            await self.admin.receive(self.message(mid=100 + i, content=f'Message {i}'))
        await self.admin.receive(self.message(uid=8, guild=self.guild, channel=self.channel, mid=999, content='Film update', mentions=[self.bot.user]))
        self.assertEqual(len(self.admin.inbox(42, kind='dm')['messages']), 50)
        self.assertEqual(len(self.admin.inbox(42, kind='dm', page=2)['messages']), 5)
        self.assertEqual(self.admin.inbox(42, guild_id='555', channel_id='111', user_id='8', query='FILM')['total'], 1)
        self.assertEqual(self.admin.inbox(42, query="' OR 1=1 --")['total'], 0)
        self.assertEqual(self.admin.inbox(42, order='oldest')['messages'][0]['message_id'], '100')
        self.assertEqual(self.admin.inbox(42)['messages'][0]['message_id'], '999')
        for filters in ({'page': 0}, {'order': 'bad'}, {'guild_id': "' OR 1=1"}):
            with self.assertRaises(UserError):
                self.admin.inbox(42, **filters)

    async def test_retention_deduplication_and_count_cap(self):
        old = self.message()
        old.created_at = datetime.fromtimestamp(time.time() - 8 * 86400, timezone.utc)
        await self.admin.receive(old)
        self.assertEqual(self.admin.inbox(42)['total'], 0)
        self.config.inbox_max_messages = 2
        for mid in (1, 1, 2, 3):
            await self.admin.receive(self.message(mid=mid))
        self.assertEqual([m['message_id'] for m in self.admin.inbox(42)['messages']], ['3', '2'])

    async def test_discord_ids_keep_full_precision(self):
        self.assertEqual(snowflake('1234567890123456789'), 1234567890123456789)
        for value in (1234567890123456789, True, '0', str(2 ** 64), []):
            with self.assertRaises(UserError):
                snowflake(value)

    async def test_normal_chat_is_single_target_deduplicated_and_kept_in_the_dm_conversation(self):
        await self.admin.receive(self.message(content='Incoming'))
        job = await self.admin.chat(42, 'Reply', user_id=7, request_id='chat-request-000001')
        self.assertEqual(await self.admin.chat(42, 'Reply', user_id=7, request_id='chat-request-000001'), job)
        with self.assertRaises(UserError):
            await self.admin.chat(42, 'Different reply', user_id=7, request_id='chat-request-000001')
        rows = self.admin.inbox(42, kind='dm', user_id='7')['messages']
        self.assertEqual(len(rows), 2)
        self.assertEqual({r['direction'] for r in rows}, {'in', 'out'})
        self.assertEqual(self.admin.history(42)['jobs'], [])
        self.assertEqual(self.admin.state(42)['threads'][0]['id'], '7')
        self.bot.is_closed.side_effect = [False, True]
        await self.admin.deliver()
        self.user.send.assert_awaited_once()
        self.assertEqual(self.admin.inbox(42, user_id='7')['messages'][0]['status'], 'sent')

    async def test_normal_chat_requires_explicit_channel_and_cannot_broadcast(self):
        for kwargs in ({'guild_ids': [555]}, {'guild_ids': [555, 556]}, {'guild_ids': [555], 'all_users': True, 'channel_id': 111}):
            with self.assertRaises(UserError):
                await self.admin.prepare(42, 'Hello', mode='normal', **kwargs)
        await self.admin.chat(42, 'Channel reply', guild_id=555, channel_id=111, request_id='channel-request-01')
        with self.store.connect() as db:
            row = db.execute('SELECT * FROM announcement_deliveries').fetchone()
        self.assertEqual((row['kind'], row['target_id']), ('channel', '111'))
        await self.admin.receive(self.message(guild=self.guild, channel=self.channel, content='Ordinary channel reply', mid=101))
        self.assertEqual(self.admin.inbox(42, kind='guild')['total'], 2)

    async def test_attachment_preview_ownership_claims_and_delivery(self):
        upload = self.admin.files.upload(42, '../photo.png', b'\x89PNG\r\n\x1a\nimage')
        self.assertEqual(upload['filename'], 'photo.png')
        self.assertTrue(upload['image'])
        with self.assertRaises(UserError):
            await self.admin.prepare(43, 'Hello', user_id=7, uploads=[upload['id']])
        plan = await self.admin.prepare(42, '', user_id=7, uploads=[upload['id']])
        self.assertEqual(plan['attachments'][0]['filename'], 'photo.png')
        self.user.send.assert_not_awaited()
        self.admin.confirm(42, plan['plan'])
        with self.assertRaises(UserError):
            await self.admin.prepare(42, 'Reuse', user_id=7, uploads=[upload['id']])
        self.bot.is_closed.side_effect = [False, True]
        await self.admin.deliver()
        call = self.user.send.await_args
        self.assertEqual(call.kwargs['files'][0].filename, 'photo.png')
        self.assertEqual(call.kwargs['allowed_mentions'].to_dict()['parse'], [])
        self.assertEqual(self.admin.history(42)['jobs'][0]['attachments'][0]['filename'], 'photo.png')

    async def test_rich_metadata_tags_embeds_and_signed_urls_stay_private(self):
        message = self.message(content='Hello <@7> **bold**')
        message.author.display_name = 'Viewer'
        message.mentions = [message.author]
        message.attachments = [SimpleNamespace(filename='photo.png', size=123, content_type='image/png', url='https://cdn.discordapp.com/attachments/222/333/photo.png?ex=signed')]
        embed = discord.Embed(title='Title', description='**Description**')
        embed.set_image(url='https://cdn.discordapp.com/attachments/222/333/photo.png')
        message.embeds = [embed]
        await self.admin.receive(message)
        row = self.admin.inbox(42)['messages'][0]
        self.assertEqual(row['tags']['user:7'], 'Viewer')
        self.assertEqual(row['attachments'][0]['filename'], 'photo.png')
        self.assertEqual(row['embeds'][0]['image'], 'embed0image')
        self.assertNotIn('signed', str(row)); self.assertNotIn('assets', row)
        message.content = 'Edited'; await self.admin.edited(message)
        self.assertEqual(self.admin.inbox(42)['messages'][0]['content'], 'Edited')
        self.admin.deleted(message.id, message.channel.id)
        row = self.admin.inbox(42)['messages'][0]
        self.assertTrue(row['deleted']); self.assertEqual(row['content'], ''); self.assertNotIn('attachments', row)

    async def test_live_notifications_are_coalesced_and_contain_revision_only(self):
        queue = self.admin.subscribe()
        await queue.get()
        await self.admin.receive(self.message(content='Private message'))
        await self.admin.receive(self.message(mid=101, content='Another private message'))
        await asyncio.sleep(0)
        self.assertEqual(queue.qsize(), 1)
        self.assertIsInstance(await queue.get(), int)
        self.admin.subscribers.discard(queue)

    async def test_concurrent_chat_retries_claim_uploaded_files_only_once(self):
        file = self.admin.files.upload(42, 'guide.txt', b'guide')
        jobs = await asyncio.gather(*(self.admin.chat(42, 'Guide', user_id=7,
            uploads=[file['id']], request_id='concurrent-chat-00001') for _ in range(2)))
        self.assertEqual(jobs[0], jobs[1])
        with self.store.connect() as db:
            self.assertEqual(db.execute('SELECT COUNT(*) FROM announcement_deliveries').fetchone()[0], 1)


class AnnouncementCommandTests(unittest.IsolatedAsyncioTestCase):
    async def test_command_defaults_to_invoking_server_channel(self):
        with tempfile.TemporaryDirectory() as temp:
            bot = SeerrBot(Config('http://seerr', 'secret', 'token', str(Path(temp) / 'cache.db'), admin_ids=frozenset([42])))
            try:
                bot.admin.require_admin = MagicMock()  # Test destination defaults independently of identity.
                bot.admin.prepare = AsyncMock(return_value={'plan': 'token', 'channels': 1, 'users': 0, 'destinations': ['Cinema'], 'skipped': []})
                interaction = MagicMock()
                interaction.user.id, interaction.guild_id, interaction.channel_id = 42, 555, 111
                interaction.response.defer = AsyncMock()
                interaction.followup.send = AsyncMock()
                await bot.tree.get_command('announce').callback(interaction, 'Hello')
                bot.admin.prepare.assert_awaited_once_with(42, 'Hello', [555], user_id=None, all_users=False, channel_id=111)
            finally:
                bot.service.api.close()
