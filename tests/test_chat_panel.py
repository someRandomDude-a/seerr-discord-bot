import asyncio
import io
import json
import tempfile
import time
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock, MagicMock, patch

from aiohttp import CookieJar, FormData
from aiohttp.test_utils import TestClient, TestServer

from media_bot.admin import AdminMessaging
from media_bot.chat import ChatFiles, FILE_LIMIT, cdn_url
from media_bot.config import Config
from media_bot.panel import ControlPanel
from media_bot.service import MediaService
from media_bot.store import Store
from media_bot.security import UserError


class ChatPanelTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.config = Config('http://seerr', 'key', 'token', str(Path(self.temp.name) / 'chat.db'))
        self.store = Store(self.config.database)
        self.user = SimpleNamespace(id=7, bot=False, send=AsyncMock())
        self.bot = SimpleNamespace(config=self.config, guilds=[], user=SimpleNamespace(id=123),
            service=SimpleNamespace(store=self.store, offload=MediaService.offload, arr={}),
            is_ready=lambda: True, get_user=lambda uid: self.user, fetch_user=AsyncMock(),
            get_channel=MagicMock(), fetch_channel=AsyncMock())
        self.bot.admin = AdminMessaging(self.bot)
        self.panel = ControlPanel(path=Path(self.temp.name) / 'settings.json')
        self.panel.bot = self.bot
        self.client = TestClient(TestServer(self.panel.application()), cookie_jar=CookieJar(unsafe=True))
        await self.client.start_server()
        self.origin = str(self.client.make_url('')).rstrip('/')

    async def asyncTearDown(self):
        await self.client.close()
        self.temp.cleanup()

    async def login(self):
        response = await self.client.post('/api/login', json={'code': self.panel.code}, headers={'Origin': self.origin})
        self.assertEqual(response.status, 200)

    async def upload(self, body=b'hello', filename='document.txt', origin=None):
        form = FormData(); form.add_field('file', io.BytesIO(body), filename=filename, content_type='application/octet-stream')
        return await self.client.post('/api/admin/uploads', data=form, headers={'Origin': origin or self.origin})

    async def test_private_media_uploads_and_events_require_cookie_authority(self):
        self.assertEqual((await self.client.get('/api/admin/files/unknown')).status, 401)
        self.assertEqual((await self.client.get('/api/admin/events')).status, 401)
        self.assertEqual((await self.upload()).status, 401)
        await self.login()
        self.assertEqual((await self.upload(origin='http://evil.example')).status, 403)

    async def test_upload_download_is_bounded_and_active_formats_never_render_inline(self):
        await self.login()
        response = await self.upload(b'<svg onload="alert(1)"></svg>', '../image.svg')
        self.assertEqual(response.status, 200)
        file = (await response.json())['result']
        self.assertFalse(file['image'])
        response = await self.client.get('/api/admin/files/' + file['id'])
        self.assertEqual(response.content_type, 'application/octet-stream')
        self.assertIn('attachment', response.headers['Content-Disposition'])
        self.assertIn('sandbox', response.headers['Content-Security-Policy'])
        self.assertEqual(response.headers['X-Content-Type-Options'], 'nosniff')
        self.assertEqual(response.headers['Cache-Control'], 'no-store')
        response = await self.upload(b'x' * (FILE_LIMIT + 1))
        self.assertEqual(response.status, 400)

    async def test_chat_uses_internal_actor_one_destination_and_idempotent_send_ids(self):
        await self.login()
        data = {'operation': 'chat', 'actor': 999, 'message': 'Reply', 'user_id': '7', 'request_id': 'panel-chat-0000001'}
        for _ in range(2):
            response = await self.client.post('/api/admin', json=data, headers={'Origin': self.origin})
            self.assertEqual(response.status, 200)
        with self.store.connect() as db:
            rows = list(db.execute('SELECT * FROM announcements'))
        self.assertEqual(len(rows), 1); self.assertEqual(rows[0]['actor_id'], 'local-panel'); self.assertEqual(rows[0]['mode'], 'normal')
        self.user.send.assert_not_awaited()  # Durable delivery queue, not an unsafe inline replay.

    async def test_event_stream_publishes_revision_without_message_content_and_stops_on_expiry(self):
        await self.login()
        response = await self.client.get('/api/admin/events')
        self.assertEqual(response.status, 200)
        self.assertEqual(response.content_type, 'text/event-stream')
        self.assertEqual(await response.content.readline(), b'event: update\n')
        self.assertIn(b'"revision":', await response.content.readline())
        await response.content.readline()
        next(iter(self.panel.sessions.values()))['expires'] = time.monotonic() - 1
        self.bot.admin.publish()
        self.assertEqual(await asyncio.wait_for(response.content.read(), timeout=2), b'')
        self.assertEqual(len(self.bot.admin.subscribers), 0)
        response.close()

    async def test_expired_and_unowned_draft_files_cannot_be_retrieved(self):
        await self.login()
        file = self.bot.admin.files.upload('other-operator', 'secret.txt', b'private')
        self.assertEqual((await self.client.get('/api/admin/files/' + file['id'])).status, 404)
        file = self.bot.admin.files.upload('local-panel', 'expired.txt', b'private')
        with self.store.connect() as db:
            db.execute('UPDATE admin_files SET expires=0 WHERE id=?', (file['id'],))
        self.assertEqual((await self.client.get('/api/admin/files/' + file['id'])).status, 404)

    def retain_image(self):
        rich = {'assets': {'attachment0': {'url': 'https://cdn.discordapp.com/attachments/222/333/photo.png?ex=old', 'filename': 'photo.png', 'image': True}}}
        with self.store.connect() as db:
            db.execute('''INSERT INTO inbox(message_id,author_id,author_name,channel_id,channel_name,content,attachment_count,created_at,rich)
                VALUES ('333','7','Viewer','222','DM','Photo',1,?,?)''', (time.time(), json.dumps(rich)))

    async def test_discord_images_are_proxied_only_from_retained_metadata(self):
        await self.login()
        self.retain_image()
        body = b'\x89PNG\r\n\x1a\nimage'
        with patch('media_bot.panel.fetch_asset', AsyncMock(return_value=body)) as fetch:
            response = await self.client.get('/api/admin/media/333/attachment0?url=http://localhost/secret')
            self.assertEqual(response.status, 200)
            self.assertEqual(response.content_type, 'image/png')
            self.assertEqual(await response.read(), body)
            fetch.assert_awaited_once_with('https://cdn.discordapp.com/attachments/222/333/photo.png?ex=old')
            self.bot.admin.deleted(333, 222)
            self.assertEqual((await self.client.get('/api/admin/media/333/attachment0')).status, 404)
            self.assertEqual(fetch.await_count, 1)

    async def test_expired_signed_assets_refresh_known_message_without_marking_it_edited(self):
        await self.login()
        self.retain_image()
        message = SimpleNamespace(id=333, channel=SimpleNamespace(id=222), content='Photo', author=SimpleNamespace(id=7, bot=False),
            mentions=[], role_mentions=[], channel_mentions=[], embeds=[],
            attachments=[SimpleNamespace(filename='photo.png', content_type='image/png', size=13,
                url='https://cdn.discordapp.com/attachments/222/333/photo.png?ex=new')])
        channel = SimpleNamespace(fetch_message=AsyncMock(return_value=message))
        self.bot.get_channel.return_value = channel
        with patch('media_bot.panel.fetch_asset', AsyncMock(side_effect=[UserError('Expired'), b'\x89PNG\r\n\x1a\nimage'])) as fetch:
            self.assertEqual((await self.client.get('/api/admin/media/333/attachment0')).status, 200)
            channel.fetch_message.assert_awaited_once_with(333)
            self.bot.get_channel.assert_called_once_with(222)
            self.assertTrue(fetch.await_args.args[0].endswith('?ex=new'))
        with self.store.connect() as db:
            self.assertIsNone(db.execute("SELECT edited_at FROM inbox WHERE message_id='333'").fetchone()[0])

    async def test_inflight_media_is_blocked_after_delete_or_session_expiry(self):
        await self.login()
        self.retain_image()
        async def deleted_during_fetch(url):
            self.bot.admin.deleted(333, 222)
            return b'\x89PNG\r\n\x1a\nimage'
        with patch('media_bot.panel.fetch_asset', deleted_during_fetch):
            self.assertEqual((await self.client.get('/api/admin/media/333/attachment0')).status, 404)
        file = self.bot.admin.files.upload('local-panel', 'photo.png', b'\x89PNG\r\n\x1a\nimage')
        next(iter(self.panel.sessions.values()))['expires'] = 0
        self.assertEqual((await self.client.get('/api/admin/files/' + file['id'])).status, 401)


class ChatAssetTests(unittest.TestCase):
    def test_cdn_allowlist_rejects_credentials_redirectors_private_hosts_and_traversal(self):
        self.assertTrue(cdn_url('https://cdn.discordapp.com/attachments/1/2/file.png?ex=signed'))
        for value in ('http://cdn.discordapp.com/attachments/1/2/f.png', 'https://localhost/attachments/1/2/f.png',
                      'https://cdn.discordapp.com.evil.example/attachments/1/2/f.png', 'https://user@cdn.discordapp.com/attachments/1/2/f.png',
                      'https://cdn.discordapp.com:443/attachments/1/2/f.png', 'https://cdn.discordapp.com/attachments/../secret',
                      'https://cdn.discordapp.com/attachments/1/%2fsecret', 'https://discord.com/redirect'):
            self.assertIsNone(cdn_url(value))

    def test_upload_limits_ownership_and_expiry(self):
        with tempfile.TemporaryDirectory() as temp:
            files = ChatFiles(Store(str(Path(temp) / 'db.sqlite')))
            file = files.upload('owner', 'safe.txt', b'data')
            for ids in ([file['id']] * 2, [{}], [file['id']] * 5):
                with self.assertRaises(UserError): files.resolve('owner', ids)
            with self.assertRaises(UserError): files.resolve('other', [file['id']])
            with self.assertRaises(UserError): files.upload('owner', 'empty', b'')
