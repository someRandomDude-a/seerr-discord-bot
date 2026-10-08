import os
import tempfile
import unittest
from pathlib import Path
from unittest.mock import AsyncMock, MagicMock, patch

from bot import SeerrBot
from media_bot.admin_ui import AdminListView, InboxDetailView, InboxReplyModal
from media_bot.config import Config
from media_bot.security import UserError
from media_bot.ui import DetailView, ItemsView, PreferencesView


class SlashModeTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.bot = SeerrBot(Config('http://seerr', 'secret', 'token', str(Path(self.temp.name) / 'cache.db'), admin_ids=frozenset([42])))
        from media_bot.jellyfin import JellyfinConfig
        self.bot.service.store.link(42, 7, 'Viewer', {'token': 'user-token', 'user_id': 'a' * 32,
            'server_id': 'b' * 32, 'device_id': 'discord-device', 'url': 'http://jellyfin:8096'})
        self.settings_patch = patch('media_bot.service.MediaService.jellyfin_settings',
            return_value=JellyfinConfig('http://jellyfin:8096', 'https://jellyfin.example', 'b' * 32))
        self.settings_patch.start()
        self.jellyfin_patch = patch('media_bot.service.JellyfinClient')
        self.jellyfin_patch.start().return_value.me.return_value = {'Id': 'a' * 32, 'ServerId': 'b' * 32}
        self.interaction = MagicMock()
        self.interaction.user.id, self.interaction.guild_id, self.interaction.channel_id = 42, 555, 111
        self.interaction.response.defer = AsyncMock()
        self.interaction.response.send_message = AsyncMock()
        self.interaction.response.send_modal = AsyncMock()
        self.interaction.followup.send = AsyncMock()
        self.interaction.edit_original_response = AsyncMock()

    async def asyncTearDown(self):
        self.bot.service.api.close()
        self.jellyfin_patch.stop()
        self.settings_patch.stop()
        self.temp.cleanup()

    async def test_toggle_requires_no_frontend_oauth_or_http_listener(self):
        self.assertIsNone(self.bot.activity)
        await self.bot.webhooks.start()
        self.assertIsNone(self.bot.webhooks.runner)
        self.assertTrue({'announce', 'servers', 'inbox', 'deliveries', 'link', 'dashboard', 'requests', 'library',
            'search', 'storage', 'notifications', 'watchlist', 'deletions', 'status'}.issubset(c.name for c in self.bot.tree.get_commands()))
        with self.assertRaises(UserError):
            await self.bot.tree.get_command('activity').callback(self.interaction)

    async def test_native_preferences_offer_full_sync_instead_of_import(self):
        self.bot.service.run = AsyncMock(return_value='Movie/series watchlists synced both ways.')
        view = PreferencesView(self.bot, 42)
        self.assertIn('Sync watchlists now', [item.label for item in view.children if hasattr(item, 'label')])
        self.assertIn('sync with Seerr automatically', str(view.render().to_dict()))
        await view.import_watchlist(self.interaction)
        self.bot.service.run.assert_awaited_once_with(42, 'sync_watchlist')

    async def test_inbox_command_local_filtered_paginated_and_private(self):
        self.bot.admin.inbox = MagicMock(return_value={'messages': [], 'total': 0})
        await self.bot.tree.get_command('inbox').callback(self.interaction, kind='dm', user_id='7', query='film')
        self.bot.admin.inbox.assert_called_with(42, kind='dm', guild_id=None, user_id='7', channel_id=None,
            query='film', order='newest', page=1, page_size=5)
        self.assertTrue(self.interaction.followup.send.await_args.kwargs['ephemeral'])

    async def test_non_operator_cannot_read_inbox_or_deliveries(self):
        self.interaction.user.id = 7
        for name in ('inbox', 'servers', 'deliveries'):
            with self.assertRaises(UserError):
                await self.bot.tree.get_command(name).callback(self.interaction)
        self.interaction.followup.send.assert_not_awaited()

    async def test_admin_views_recheck_revocation_and_owner(self):
        view = AdminListView(self.bot, 42, 'inbox')
        self.interaction.user.id = 7
        self.assertFalse(await view.interaction_check(self.interaction))
        self.interaction.user.id = 42
        self.bot.config.admin_ids = frozenset()
        # Slow live authorization belongs after defer, not in the 3-second
        # interaction check (which must also permit opening a reply modal).
        with self.assertRaises(UserError):
            await view.refresh(self.interaction)
        self.interaction.response.defer.assert_awaited_once()
        self.interaction.edit_original_response.assert_not_awaited()

    async def test_multiserver_announcement_preserves_selected_targets(self):
        self.bot.admin.prepare = AsyncMock(return_value={'plan': 'p', 'channels': 2, 'users': 0, 'destinations': [], 'skipped': []})
        await self.bot.tree.get_command('announce').callback(self.interaction, 'Update', server_ids='1234567890123456789,1234567890123456790')
        self.bot.admin.prepare.assert_awaited_once_with(42, 'Update', [1234567890123456789, 1234567890123456790], user_id=None, all_users=False, channel_id=None)

    async def test_dm_reply_previews_instead_of_sending(self):
        self.bot.admin.prepare = AsyncMock(return_value={'plan': 'p', 'channels': 0, 'users': 1, 'destinations': ['Viewer'], 'skipped': []})
        source = {'guild_id': None, 'author_id': '7', 'channel_id': '8'}
        modal = InboxReplyModal(self.bot, 42, source)
        modal.message._value = 'A reply'
        await modal.on_submit(self.interaction)
        self.bot.admin.prepare.assert_awaited_once_with(42, 'A reply', [], user_id=7, channel_id=None)
        with self.bot.service.store.connect() as db:
            self.assertEqual(db.execute('SELECT COUNT(*) FROM announcements').fetchone()[0], 0)

    async def test_admin_delivery_details_are_available_without_activity(self):
        with self.bot.service.store.connect() as db:
            job = db.execute("INSERT INTO announcements(actor_id,content,created_at) VALUES ('42','Update',0)").lastrowid
            db.execute("INSERT INTO announcement_deliveries(announcement_id,kind,target_id,label,status,error) VALUES (?,'user','7','Viewer','failed','Blocked')", (job,))
        await self.bot.tree.get_command('deliveries').callback(self.interaction, job=job)
        result = self.interaction.followup.send.await_args.kwargs['embed']
        self.assertIn('failed', result.fields[0].value)
        self.assertIn('Blocked', result.fields[0].value)

    async def test_native_views_fit_discord_limits(self):
        self.bot.admin.inbox = MagicMock(return_value={'messages': [{
            'message_id': str(i), 'author_id': '7', 'author_name': 'x' * 100,
            'guild_id': '555', 'guild_name': 'y' * 100, 'channel_id': '111', 'channel_name': 'z' * 100,
            'content': '@everyone <script>' * 300, 'attachment_count': 1, 'created_at': 1,
        } for i in range(5)], 'total': 100})
        view = AdminListView(self.bot, 42, 'inbox'); await view.load()
        self.assertLessEqual(len(view.render()), 6000)
        detail = InboxDetailView(self.bot, 42, view.rows[0], view)
        self.assertLessEqual(len(detail.render().description), 4096)
        self.assertLessEqual(len(detail.render()), 6000)
        self.assertLessEqual(len(view.children), 25)

    async def test_library_title_filter_refreshes_and_jellyfin_open_reauthorizes(self):
        self.bot.service.run = AsyncMock(return_value=[{'kind': 'movie', 'external_id': '10', 'title': 'A film', 'available': True}])
        view = ItemsView(self.bot, 42, 'library', ('all',), query='FILM')
        await view.load()
        self.assertEqual(len(view.items), 1)
        self.bot.service.run.assert_awaited_with(42, 'library', 'all')
        detail = DetailView(self.bot, 42, view.items[0], view)
        self.bot.service.run.return_value = 'https://jellyfin.example/web/index.html#!/details?id=abc'
        await detail.open(self.interaction)
        self.bot.service.run.assert_awaited_with(42, 'open', view.items[0])

    async def test_native_gallery_has_numbered_posters_and_fast_access_checked_paging(self):
        self.bot.service.run = AsyncMock(return_value=[{'kind': 'movie', 'external_id': str(i), 'title': f'Film {i}', 'available': True, 'poster_path': '/a.jpg'} for i in range(1, 16)])
        self.bot.service.validate_browse = MagicMock()
        view = ItemsView(self.bot, 42, 'library', ('all',))
        await view.load()
        cards = view.gallery()
        self.assertEqual(len(cards), 6)
        self.assertEqual(cards[1].thumbnail.url, 'https://image.tmdb.org/t/p/w185/a.jpg')
        self.assertTrue(cards[1].title.startswith('1.'))
        self.assertLessEqual(sum(len(card) for card in cards), 6000)
        await view.next(self.interaction)
        self.assertEqual(view.page, 1)
        self.bot.service.run.assert_awaited_once()
        self.bot.service.validate_browse.assert_called_once_with(42, False, True)
        self.assertIn('embeds', self.interaction.edit_original_response.await_args.kwargs)

    async def test_native_search_next_moves_to_more_upstream_results(self):
        self.bot.service.run = AsyncMock(return_value=[{'kind': 'movie', 'external_id': '10', 'title': 'A film'}])
        view = ItemsView(self.bot, 42, 'search', ('movie', 'Title', 1))
        await view.load()
        await view.next(self.interaction)
        self.bot.service.run.assert_awaited_with(42, 'search', 'movie', 'Title', 2)
        self.assertEqual(view.page, 0)

    async def test_native_filter_keeps_full_gallery_and_back_does_not_resync(self):
        self.bot.service.run = AsyncMock(return_value=[{'kind': 'movie', 'external_id': str(i), 'title': f'Film {i}'} for i in range(1, 10)])
        self.bot.service.validate_browse = MagicMock()
        view = ItemsView(self.bot, 42, 'library', ('all',))
        await view.load()
        view.query = 'Film 9'; view.filter_items()
        self.assertEqual(len(view.items), 1)
        view.query = ''; view.filter_items()
        self.assertEqual(len(view.items), 9)
        detail = DetailView(self.bot, 42, view.items[0], view)
        await detail.back(self.interaction)
        self.bot.service.run.assert_awaited_once()

    async def test_native_gallery_does_not_render_posters_after_access_rejection(self):
        self.bot.service.run = AsyncMock(return_value=[{'kind': 'movie', 'external_id': '10', 'title': 'Private film', 'poster_path': '/a.jpg'}])
        self.bot.service.validate_browse = MagicMock(side_effect=UserError('Run /link again.'))
        view = ItemsView(self.bot, 42, 'library', ('all',))
        await view.load()
        with self.assertRaises(UserError):
            await view.show_page(self.interaction)
        self.interaction.edit_original_response.assert_not_awaited()

    async def test_untrusted_image_paths_are_not_embedded_in_native_gallery(self):
        self.bot.service.run = AsyncMock(return_value=[{'kind': 'movie', 'external_id': '10', 'title': 'Film', 'poster_path': '/../../private?key=secret'}])
        view = ItemsView(self.bot, 42, 'library', ('all',))
        await view.load()
        self.assertIsNone(view.gallery()[1].thumbnail.url)


class EnvironmentTests(unittest.TestCase):
    def env(self, temp, **changes):
        return {'SEERR_URL': 'http://seerr', 'SEERR_ADMIN_KEY': 'key', 'DISCORD_TOKEN': 'token', 'DATA_DIR': temp, **changes}

    def test_slash_only_configuration_needs_no_activity_credentials(self):
        with tempfile.TemporaryDirectory() as temp, patch.dict(os.environ, self.env(temp), clear=True):
            config = Config.from_env()
            self.assertFalse(config.activity_enabled)
            self.assertEqual(config.client_secret, '')

    def test_bad_boolean_ids_port_and_activity_credentials_fail_closed(self):
        for change in ({'ACTIVITY_ENABLED': 'yes'}, {'ENABLE_MEMBERS_INTENT': 'tru'}, {'ADMIN_DISCORD_IDS': '-1'},
                       {'INBOX_CHANNEL_IDS': 'name'}, {'ADMIN_ANNOUNCEMENT_CHANNELS': '{"0":"1"}'},
                       {'BOT_PORT': '65536'}, {'ACTIVITY_ENABLED': 'true'}):
            with self.subTest(change=change), tempfile.TemporaryDirectory() as temp:
                with patch.dict(os.environ, self.env(temp, **change), clear=True), self.assertRaises(ValueError):
                    Config.from_env()

    def test_every_example_option_has_inline_documentation_and_is_consumed(self):
        root = Path(__file__).resolve().parent.parent
        example = (root / '.env.example').read_text(encoding='utf-8').splitlines() + (root / '.env.advanced.example').read_text(encoding='utf-8').splitlines()
        config = (root / 'media_bot/config.py').read_text(encoding='utf-8') + (root / 'bot.py').read_text(encoding='utf-8')
        for index, line in enumerate(example):
            if not line or line.startswith('#'):
                continue
            name = line.split('=', 1)[0]
            self.assertTrue(example[index - 1].startswith('#'), name)
            if name.startswith(('RADARR_', 'SONARR_', 'LIDARR_', 'READARR_')):
                self.assertIn(name.split('_', 1)[1], config, name)
            else:
                self.assertIn(name, config, name)
