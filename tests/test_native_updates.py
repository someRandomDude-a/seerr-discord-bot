"""Native component updates, using Discord response types and a shared message."""
import asyncio
import unittest
from types import SimpleNamespace
from unittest.mock import AsyncMock, MagicMock

import discord
from discord.ui.view import ViewStore

from media_bot.admin_ui import AdminListView, InboxDetailView
from media_bot.security import UserError
from media_bot.ui import (ActionResultView, AnnouncementConfirmView, CollectionFilterModal,
    ConfirmView, DashboardView, DetailView, ItemsView, PreferencesView, QuickConnectView)


def interaction(message, values=(), kind=discord.InteractionType.component, owner=42):
    result = SimpleNamespace(user=SimpleNamespace(id=owner), type=kind, data={'values': list(values)},
        message=message, followup=SimpleNamespace(send=AsyncMock()))
    response = SimpleNamespace(type=None)
    response.is_done = lambda: response.type is not None

    def apply(**kwargs):
        if 'embed' in kwargs:
            message.embeds = [kwargs['embed']] if kwargs['embed'] else []
        for key in ('content', 'embeds', 'view'):
            if key in kwargs:
                setattr(message, key, kwargs[key])
        return message

    async def edit(**kwargs):
        assert not response.is_done(), 'Interaction acknowledged twice'
        if 'view' in kwargs:
            message.store.remove_message_tracking(1)
        response.type = discord.InteractionResponseType.message_update
        return apply(**kwargs)

    async def defer(**kwargs):
        assert not response.is_done(), 'Interaction acknowledged twice'
        response.type = (discord.InteractionResponseType.deferred_channel_message if
            kind == discord.InteractionType.application_command or kwargs.get('thinking') else
            discord.InteractionResponseType.deferred_message_update)

    async def send(*args, **kwargs):
        assert not response.is_done(), 'Interaction acknowledged twice'
        response.type = discord.InteractionResponseType.channel_message

    async def modal(value):
        assert not response.is_done(), 'Interaction acknowledged twice'
        response.type = discord.InteractionResponseType.modal

    async def update(**kwargs):
        assert response.is_done(), 'Final edit before acknowledgement'
        view = kwargs.get('view')
        if view and not view.is_finished():
            message.store.add_view(view, 1)
        return apply(**kwargs)

    response.edit_message = AsyncMock(side_effect=edit)
    response.defer = AsyncMock(side_effect=defer)
    response.send_message = AsyncMock(side_effect=send)
    response.send_modal = AsyncMock(side_effect=modal)
    result.response = response
    result.edit_original_response = AsyncMock(side_effect=update)
    return result


class NativeUpdateTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        async def offload(callback, *args, **kwargs):
            return callback(*args, **kwargs)
        self.account = {'name': 'Viewer', 'opted_in': False, 'device': 'Browser'}
        self.bot = SimpleNamespace(guard=MagicMock(), config=SimpleNamespace(devices={}, link_ttl=120),
            service=SimpleNamespace(run=AsyncMock(), offload=offload, account=lambda owner: self.account,
                validate_browse=MagicMock(), verified_account=AsyncMock(), devices=MagicMock(return_value={'Browser': None}),
                unwatch=AsyncMock(return_value='Removed.'), cancel_deletion=AsyncMock(return_value='Undone.'),
                mute_notifications=MagicMock()),
            linking=SimpleNamespace(cancel=AsyncMock(return_value=True)),
            admin=SimpleNamespace(inbox=MagicMock(), state=MagicMock(), deliveries=MagicMock(),
                prepare=AsyncMock(), confirm=MagicMock(return_value=10), require_admin=MagicMock(), plans={}))
        self.rows = [{'kind': 'movie', 'external_id': str(i), 'title': f'Film {i}', 'available': True} for i in range(12)]
        self.bot.service.run.return_value = self.rows
        self.message = SimpleNamespace(content=None, embeds=[], view=None, store=ViewStore(SimpleNamespace()))

    def display(self, view):
        # Exercise the actual Discord view registry, not just callback mocks.
        self.message.store = ViewStore(SimpleNamespace())
        self.message.store.add_view(view, 1)
        self.message.view = view
        self.message.embeds = view.gallery() if isinstance(view, ItemsView) else [view.render()] if hasattr(view, 'render') else []

    async def click(self, label=None, values=(), select=0, owner=42):
        view = self.message.view
        item = next(child for child in view.children if getattr(child, 'label', None) == label) if label else [
            child for child in view.children if isinstance(child, discord.ui.Select)][select]
        event = interaction(self.message, values, owner=owner)
        if await view.interaction_check(event):
            await item.callback(event)
        return event

    def assert_updated(self, event):
        event.response.edit_message.assert_awaited_once()
        loading = event.response.edit_message.await_args.kwargs
        self.assertEqual(loading['embeds'][0].title, 'Updating…')
        self.assertIsNone(loading['view'])
        event.edit_original_response.assert_awaited_once()
        event.followup.send.assert_not_awaited()
        self.assertNotEqual(self.message.embeds[0].title, 'Updating…')
        if self.message.view is not None:
            self.assertIs(self.message.store._synced_message_views[1], self.message.view)
            # Discord's gateway echo must refresh the replacement, not the old
            # view (and stopping an old view must not untrack the replacement).
            self.message.store.update_from_message(1, self.message.view.to_components())

    async def gallery(self, operation='library', args=('all',)):
        view = ItemsView(self.bot, 42, operation, args)
        await view.load()
        self.display(view)
        return view

    async def test_pagination_and_back_update_on_the_first_click(self):
        view = await self.gallery()
        event = await self.click('Next →')
        self.assert_updated(event)
        self.assertEqual(self.message.view.page, 1)
        self.assertEqual(view.page, 0)
        event = await self.click('← Previous')
        self.assert_updated(event)
        self.assertEqual(self.message.view.page, 0)
        self.bot.service.run.return_value = self.rows[2]
        event = await self.click(values=['2'])
        self.assert_updated(event)
        self.assertIsInstance(self.message.view, DetailView)
        self.assertIn('Film 2', self.message.embeds[0].title)
        event = await self.click('← Back')
        self.assert_updated(event)
        self.assertIsInstance(self.message.view, ItemsView)

    async def test_category_selection_uses_the_interaction_payload_not_shared_select_values(self):
        old = await self.gallery()
        category = [child for child in old.children if isinstance(child, discord.ui.Select)][1]
        category._values = ['movie']  # Discord's mutable component state from another click.
        event = await self.click(values=['tv'], select=1)
        self.assert_updated(event)
        self.bot.service.run.assert_awaited_with(42, 'library', 'tv')
        self.assertEqual(self.message.view.args, ('tv',))
        self.assertEqual(old.args, ('all',))
        defaults = [option.value for option in self.message.view.children[-1].options if option.default]
        self.assertEqual(defaults, ['tv'])

    async def test_request_scope_and_remote_batches_refresh_immediately(self):
        await self.gallery('requests', (False,))
        event = await self.click('All requests')
        self.assert_updated(event)
        self.bot.service.run.assert_awaited_with(42, 'requests', True)
        self.bot.service.run.return_value = self.rows[:1]
        await self.gallery('search', ('movie', 'Film', 1))
        event = await self.click('Next →')
        self.assert_updated(event)
        self.assertEqual(self.message.view.args[-1], 2)
        event = await self.click('← Previous')
        self.assert_updated(event)
        self.assertEqual(self.message.view.args[-1], 1)

    async def test_filter_and_search_modals_update_the_originating_message(self):
        await self.gallery()
        opened = await self.click('Filter titles')
        modal = opened.response.send_modal.await_args.args[0]
        self.assertIsInstance(modal, CollectionFilterModal)
        modal.query._value = 'Film 9'
        event = interaction(self.message, kind=discord.InteractionType.modal_submit)
        await modal.on_submit(event)
        self.assert_updated(event)
        self.assertEqual([row['title'] for row in self.message.view.items], ['Film 9'])
        opened = await self.click('Search new titles')
        modal = opened.response.send_modal.await_args.args[0]
        modal.query._value = 'New title'
        event = interaction(self.message, kind=discord.InteractionType.modal_submit)
        await modal.on_submit(event)
        self.assert_updated(event)
        self.bot.service.run.assert_awaited_with(42, 'search', 'movie', 'New title', 1)
        self.assertEqual(self.message.view.operation, 'search')

    async def test_slow_update_shows_loading_and_does_not_need_or_queue_a_second_click(self):
        view = await self.gallery()
        next_button = next(child for child in view.children if getattr(child, 'label', None) == 'Next →')
        entered, release = asyncio.Event(), asyncio.Event()
        async def validate():
            entered.set()
            await release.wait()
        view.validate = validate
        first = interaction(self.message)
        task = asyncio.create_task(next_button.callback(first))
        await entered.wait()
        try:
            self.assertEqual(self.message.embeds[0].title, 'Updating…')
            self.assertIsNone(self.message.view)
            second = interaction(self.message)
            await next_button.callback(second)
            second.response.send_message.assert_awaited_once()
            self.assertIn('updating', second.response.send_message.await_args.args[0])
            second.edit_original_response.assert_not_awaited()
        finally:
            release.set()
            await task
        self.assert_updated(first)
        self.assertEqual(self.message.view.page, 1)
        third = interaction(self.message)
        await next_button.callback(third)
        self.assertIn('replaced', third.response.send_message.await_args.args[0])
        self.assertEqual(self.message.view.page, 1)

    async def test_selection_is_captured_before_any_network_wait(self):
        view = await self.gallery()
        choice = next(child for child in view.children if isinstance(child, discord.ui.Select))
        choice._values = ['8']
        self.bot.service.run.return_value = self.rows[1]
        event = await self.click(values=['1'])
        self.assert_updated(event)
        self.bot.service.run.assert_awaited_with(42, 'details', self.rows[1])

    async def test_failed_category_load_does_not_mutate_snapshot_or_leave_a_spinner(self):
        old = await self.gallery()
        self.bot.service.run.side_effect = UserError('Verification unavailable.')
        event = await self.click(values=['tv'], select=1)
        self.assert_updated(event)
        self.assertEqual(old.args, ('all',))
        self.assertEqual(old.page, 0)
        self.assertEqual(self.message.embeds[0].title, 'Unable to update')
        self.assertNotIn('Film', str(self.message.embeds[0].to_dict()))
        self.assertIsNone(self.message.view)
        self.assertFalse(old.flow.busy)

    async def test_revoked_access_and_foreign_users_never_render_cached_gallery(self):
        await self.gallery()
        foreign = await self.click('Next →', owner=99)
        foreign.response.edit_message.assert_not_awaited()
        self.bot.service.validate_browse.side_effect = UserError('Run /link again.')
        event = await self.click('Next →')
        self.assert_updated(event)
        self.assertNotIn('Film', str(self.message.embeds[0].to_dict()))

    async def test_follow_and_unfollow_replace_stale_buttons_and_back_reloads(self):
        for operation, args, label in [('library', ('all',), '⭐ Follow'), ('watches', (), '☆ Unfollow')]:
            with self.subTest(operation=operation):
                parent = ItemsView(self.bot, 42, operation, args)
                detail = DetailView(self.bot, 42, self.rows[0], parent)
                self.display(detail)
                self.bot.service.run.return_value = 'Saved.'
                event = await self.click(label)
                self.assert_updated(event)
                self.assertIsInstance(self.message.view, ActionResultView)
                self.assertNotIn(label, [getattr(child, 'label', '') for child in self.message.view.children])
                self.bot.service.run.return_value = self.rows
                self.bot.service.watches = AsyncMock(return_value=[])
                event = await self.click('← Back')
                self.assert_updated(event)
                self.assertIsInstance(self.message.view, ItemsView)
                if operation == 'watches':
                    self.bot.service.watches.assert_awaited_once_with(42)
                    self.assertEqual(self.message.view.items, [])
                else:
                    self.bot.service.run.assert_awaited_with(42, 'library', 'all')

    async def test_request_delete_and_confirmation_keep_one_message_and_claim_once(self):
        for operation, label in [('search', '＋ Request'), ('requests', 'Request file deletion')]:
            with self.subTest(operation=operation):
                row = {**self.rows[0], 'id': 8, 'deletable': True, 'requestable': True}
                parent = ItemsView(self.bot, 42, operation, ('movie', 'Film', 1) if operation == 'search' else (False,))
                self.display(DetailView(self.bot, 42, row, parent))
                event = await self.click(label)
                self.assert_updated(event)
                confirmation = self.message.view
                self.assertIsInstance(confirmation, ConfirmView)
                button = confirmation.children[0]
                self.bot.service.run.return_value = 'Recorded.'
                event = await self.click('Confirm')
                self.assert_updated(event)
                self.assertIsInstance(self.message.view, ActionResultView)
                calls = self.bot.service.run.await_count
                await button.callback(interaction(self.message))
                self.assertEqual(self.bot.service.run.await_count, calls)

    async def test_cancel_and_undo_replace_the_message_without_stale_actions(self):
        view = ConfirmView(self.bot, 42, 'request', (self.rows[0],), 'Confirm?', 'Preview')
        self.display(view)
        event = await self.click('Cancel')
        self.assert_updated(event)
        self.bot.service.run.assert_not_awaited()
        row = {**self.rows[0], 'id': 7, 'status': 'pending', 'execute_after': 100}
        self.display(DetailView(self.bot, 42, row, ItemsView(self.bot, 42, 'deletions')))
        event = await self.click('Undo file deletion')
        self.assert_updated(event)
        self.bot.service.cancel_deletion.assert_awaited_once_with(42, 7)
        self.assertEqual(self.message.embeds[0].title, 'Deletion undone')

    async def test_preferences_device_opt_in_sync_and_mute_refresh_inline(self):
        devices = {'Browser': None, 'Living room': 'https://jellyfin.example'}
        async def preference(owner, operation, enabled=None, device=None):
            if device:
                self.account['device'] = device
            if enabled is not None:
                self.account['opted_in'] = enabled
            return 'Synced.'
        self.bot.service.run.side_effect = preference
        self.display(PreferencesView(self.bot, 42, devices))
        event = await self.click(values=['Living room'])
        self.assert_updated(event)
        self.assertIn('Living room', str(self.message.embeds[0].to_dict()))
        select = next(child for child in self.message.view.children if isinstance(child, discord.ui.Select))
        self.assertEqual([option.value for option in select.options if option.default], ['Living room'])
        event = await self.click('🔔 Opt in to DMs')
        self.assert_updated(event)
        self.assertIn('Enabled', str(self.message.embeds[0].to_dict()))
        event = await self.click('Sync watchlists now')
        self.assert_updated(event)
        self.assertIn('Synced.', str(self.message.embeds[0].to_dict()))
        self.bot.service.account = MagicMock(side_effect=UserError('Revoked'))
        event = await self.click('Mute all updates')
        self.assert_updated(event)
        self.bot.service.account.assert_not_called()
        self.assertNotIn('Living room', str(self.message.embeds[0].to_dict()))

    async def test_stale_modal_cannot_restore_a_replaced_view(self):
        await self.gallery()
        opened = await self.click('Filter titles')
        modal = opened.response.send_modal.await_args.args[0]
        await self.click('Next →')
        event = interaction(self.message, kind=discord.InteractionType.modal_submit)
        await modal.on_submit(event)
        event.response.send_message.assert_awaited_once()
        self.assertEqual(self.message.view.page, 1)
        event.edit_original_response.assert_not_awaited()

    async def test_dashboard_and_home_transitions_share_the_same_message_flow(self):
        self.display(DashboardView(self.bot, 42))
        event = await self.click(values=['tv'])
        self.assert_updated(event)
        self.bot.service.run.assert_awaited_with(42, 'library', 'tv')
        old = self.message.view
        self.bot.service.run.return_value = {'account': self.account, 'requests': 1, 'library': 2, 'meta': {}}
        event = await self.click('⌂ Home')
        self.assert_updated(event)
        self.assertIsInstance(self.message.view, DashboardView)
        self.assertIs(old.flow, self.message.view.flow)
        self.assertIs(old.flow.current, self.message.view)

    async def test_every_dashboard_data_button_refreshes_the_source_message(self):
        self.bot.service.watches = AsyncMock(return_value=self.rows)
        self.bot.service.deletions = AsyncMock(return_value=[])
        data = {'account': self.account, 'requests': 1, 'library': 2, 'meta': {}}
        for label, result, expected in [
            ('📋 My requests', self.rows, ItemsView), ('✨ Discover', self.rows, ItemsView),
            ('💽 Storage', {'radarr': [{'path': '/media', 'freeSpace': 10, 'totalSpace': 20}]}, DashboardView),
            ('🔔 Notifications', None, PreferencesView), ('⭐ Watchlist', None, ItemsView),
            ('🕒 Scheduled deletions', None, ItemsView), ('↻ Refresh', data, DashboardView),
        ]:
            with self.subTest(button=label):
                self.bot.service.run.return_value = result
                self.display(DashboardView(self.bot, 42))
                event = await self.click(label)
                self.assert_updated(event)
                self.assertIsInstance(self.message.view, expected)

    async def test_discover_category_updates_heading_and_gallery_refresh_updates_results(self):
        await self.gallery('discover', ('movie', 1))
        event = await self.click(values=['tv'], select=1)
        self.assert_updated(event)
        self.assertIn('series', self.message.embeds[0].title)
        self.bot.service.run.return_value = self.rows[:1]
        event = await self.click('↻ Refresh')
        self.assert_updated(event)
        self.assertEqual(len(self.message.view.items), 1)
        self.assertEqual(self.message.view.args, ('tv', 1))

    async def test_deletion_selection_rechecks_state_instead_of_rendering_cached_undo(self):
        row = {**self.rows[0], 'id': 7, 'status': 'pending', 'execute_after': 100}
        self.bot.service.deletions = AsyncMock(return_value=[row])
        await self.gallery('deletions', ())
        self.bot.service.deletions.return_value = [{**row, 'status': 'cancelled', 'subtitle': 'Cancelled'}]
        event = await self.click(values=['0'])
        self.assert_updated(event)
        self.assertNotIn('Undo file deletion', [getattr(child, 'label', None) for child in self.message.view.children])
        self.assertIn('Cancelled', str(self.message.embeds[0].to_dict()))

    async def test_slow_confirmation_and_simultaneous_cancel_cannot_race_or_repeat_the_write(self):
        confirmation = ConfirmView(self.bot, 42, 'request', (self.rows[0],), 'Confirm?', 'Preview')
        self.display(confirmation)
        entered, release = asyncio.Event(), asyncio.Event()
        async def record(*args):
            entered.set()
            await release.wait()
            return 'Recorded.'
        self.bot.service.run.side_effect = record
        first = interaction(self.message)
        task = asyncio.create_task(confirmation.children[0].callback(first))
        await entered.wait()
        try:
            for child in confirmation.children:
                second = interaction(self.message)
                await child.callback(second)
                second.response.send_message.assert_awaited_once()
                second.edit_original_response.assert_not_awaited()
            self.bot.service.run.assert_awaited_once()
        finally:
            release.set()
            await task
        self.assert_updated(first)
        self.assertEqual(self.message.embeds[0].title, 'Action recorded')

    async def test_failed_confirmation_finishes_loading_without_exposing_or_replaying_the_action(self):
        confirmation = ConfirmView(self.bot, 42, 'request', (self.rows[0],), 'Confirm?', 'Preview')
        self.display(confirmation)
        self.bot.service.run.side_effect = UserError('Request not confirmed. Check your requests before retrying.')
        button = confirmation.children[0]
        event = await self.click('Confirm')
        self.assert_updated(event)
        self.assertEqual(self.message.embeds[0].title, 'Unable to update')
        self.assertIsNone(self.message.view)
        await button.callback(interaction(self.message))
        self.bot.service.run.assert_awaited_once()

    async def test_inbox_selection_paging_and_modal_preview_refresh_the_original(self):
        rows = [{'message_id': str(i), 'author_name': f'Person {i}', 'author_id': '7', 'guild_id': None,
            'guild_name': '', 'channel_name': '', 'channel_id': '8', 'created_at': 1,
            'content': f'Message {i}', 'attachment_count': 0} for i in range(5)]
        self.bot.admin.inbox.return_value = {'messages': rows, 'total': 10}
        parent = AdminListView(self.bot, 42, 'inbox')
        await parent.load()
        self.display(parent)
        event = await self.click('→')
        self.assert_updated(event)
        self.assertEqual(self.message.view.page, 2)
        event = await self.click(values=['3'])
        self.assert_updated(event)
        self.assertIsInstance(self.message.view, InboxDetailView)
        self.assertIn('Message 3', self.message.embeds[0].description)
        opened = await self.click('Reply…')
        modal = opened.response.send_modal.await_args.args[0]
        modal.message._value = 'Reply'
        self.bot.admin.prepare.return_value = {'plan': 'p', 'channels': 0, 'users': 1, 'destinations': [], 'skipped': []}
        event = interaction(self.message, kind=discord.InteractionType.modal_submit)
        await modal.on_submit(event)
        self.assert_updated(event)
        self.assertIsInstance(self.message.view, AnnouncementConfirmView)
        event = await self.click('Send')
        self.assert_updated(event)
        self.bot.admin.confirm.assert_called_once_with(42, 'p')
        self.assertIsNone(self.message.view)

    async def test_delivery_job_selection_refreshes_destinations(self):
        self.bot.admin.state.return_value = {'jobs': [{'id': 9, 'sent': 1, 'pending': 0, 'failed': 0, 'uncertain': 0}]}
        self.bot.admin.deliveries.return_value = {'total': 1, 'rows': [
            {'label': 'Recipient', 'status': 'sent', 'target_id': '7', 'error': ''}]}
        parent = AdminListView(self.bot, 42, 'deliveries')
        await parent.load()
        self.display(parent)
        event = await self.click(values=['9'])
        self.assert_updated(event)
        self.assertEqual(self.message.view.job, 9)
        self.bot.admin.deliveries.assert_called_once_with(42, 9, 1)
        self.assertIn('Recipient', str(self.message.embeds[0].to_dict()))

    async def test_quick_connect_cancel_is_acknowledged_and_updated_immediately(self):
        self.display(QuickConnectView(self.bot, 42, SimpleNamespace(generation='g')))
        event = await self.click('Cancel connection')
        event.response.defer.assert_awaited_once_with(ephemeral=True, thinking=False)
        event.edit_original_response.assert_awaited_once()
        event.followup.send.assert_not_awaited()
        self.bot.linking.cancel.assert_awaited_once_with(42, 'g')
        self.assertEqual(self.message.embeds[0].title, 'Connection cancelled')

    async def test_quick_connect_cancel_cannot_overwrite_a_completed_link(self):
        self.display(QuickConnectView(self.bot, 42, SimpleNamespace(generation='g')))
        self.bot.linking.cancel.return_value = False
        before = self.message.embeds
        event = await self.click('Cancel connection')
        event.response.defer.assert_awaited_once_with(ephemeral=True, thinking=False)
        event.edit_original_response.assert_not_awaited()
        event.followup.send.assert_awaited_once()
        self.assertIs(self.message.embeds, before)

    async def test_open_link_uses_a_thinking_followup_and_does_not_replace_gallery_state(self):
        parent = ItemsView(self.bot, 42, 'library', ('all',))
        detail = DetailView(self.bot, 42, self.rows[0], parent)
        self.display(detail)
        self.bot.service.run.return_value = 'https://jellyfin.example/web/'
        event = await self.click('▶ Open in Jellyfin')
        event.response.defer.assert_awaited_once_with(ephemeral=True, thinking=True)
        event.followup.send.assert_awaited_once()
        event.edit_original_response.assert_not_awaited()
        self.assertIs(self.message.view, detail)
