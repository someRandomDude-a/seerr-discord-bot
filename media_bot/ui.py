import math
import re
import time
import discord

from .security import UserError

COLOR = 0xA5B4FC
ICONS = {'movie': '🎬', 'tv': '📺', 'music': '🎵', 'book': '📚'}


def clean(value, limit=200):
    return discord.utils.escape_markdown(discord.utils.escape_mentions(str(value)))[:limit]


def embed(title, description=None):
    result = discord.Embed(title=title[:256], description=description, color=COLOR)
    result.set_author(name='Media')
    result.set_footer(text='Private')
    return result


def poster_url(item, size='w185'):
    path = item.get('poster_path')
    if isinstance(path, str) and re.fullmatch(r'/[A-Za-z0-9_-]{1,100}\.(?:jpg|png|webp)', path):
        return f'https://image.tmdb.org/t/p/{size}{path}'
    return None


def bytes_label(value):
    value = max(0, int(value or 0))
    for suffix in ('B', 'KiB', 'MiB', 'GiB', 'TiB', 'PiB'):
        if value < 1024:
            return f'{value:.1f} {suffix}'
        value /= 1024
    return f'{value:.1f} EiB'


async def error_message(interaction, message):
    if interaction.response.is_done():
        await interaction.followup.send(message, ephemeral=True)
    else:
        await interaction.response.send_message(message, ephemeral=True)


class OwnedView(discord.ui.View):
    def __init__(self, bot, owner, timeout=300):
        super().__init__(timeout=timeout)
        self.bot, self.owner = bot, owner

    async def interaction_check(self, interaction):
        if interaction.user.id != self.owner:
            await error_message(interaction, 'Open your own /dashboard.')
            return False
        try:
            self.bot.guard(interaction)
            return True
        except UserError as exc:
            await error_message(interaction, str(exc))
            return False

    async def on_error(self, interaction, error, item):
        await error_message(interaction, str(error) if isinstance(error, UserError) else 'Something went wrong. No success was confirmed. Refresh and check /status.')

    def button(self, label, callback, style=discord.ButtonStyle.secondary, row=None, disabled=False):
        button = discord.ui.Button(label=label, style=style, row=row, disabled=disabled)
        button.callback = callback
        self.add_item(button)
        return button


class QuickConnectView(OwnedView):
    def __init__(self, bot, owner, session):
        super().__init__(bot, owner, timeout=bot.config.link_ttl)
        self.session = session
        self.button('Cancel connection', self.cancel)

    async def cancel(self, interaction):
        await interaction.response.defer(ephemeral=True)
        cancelled = await self.bot.linking.cancel(self.owner, self.session.generation)
        if not cancelled:
            raise UserError('This connection already finished or was replaced. Check /dashboard or run /link.')
        await interaction.edit_original_response(embed=embed('Connection cancelled', 'No new account was linked. Run /link whenever you are ready.'), view=None)
        self.stop()


class AnnouncementConfirmView(OwnedView):
    def __init__(self, bot, owner, plan):
        super().__init__(bot, owner, timeout=120)
        self.plan = plan
        self.used = False
        self.button('Send', self.send, discord.ButtonStyle.primary)
        self.button('Cancel', self.cancel)

    def render(self):
        result = embed('Send announcement?', f"**{self.plan['channels']} channels · {self.plan['users']} user DMs**\nMentions disabled.")
        result.set_footer(text='Operator preview · Private')
        if self.plan.get('message'):
            result.add_field(name='Message', value=clean(self.plan['message'], 1000), inline=False)
        if self.plan['destinations']:
            result.add_field(name='Recipients', value='\n'.join(clean(label, 80) for label in self.plan['destinations'])[:1000], inline=False)
        if self.plan['skipped']:
            result.add_field(name='Unavailable', value=clean(', '.join(self.plan['skipped']), 500), inline=False)
        return result

    async def send(self, interaction):
        if self.used:
            raise UserError('This preview was already used.')
        self.used = True
        await interaction.response.defer(ephemeral=True)
        job = await self.bot.service.offload(self.bot.admin.confirm, self.owner, self.plan['plan'])
        await interaction.edit_original_response(embed=embed('Announcement started', f'#{job} · /deliveries job:{job}'), view=None)
        self.stop()

    async def cancel(self, interaction):
        self.used = True
        self.bot.admin.plans.pop(self.plan['plan'], None)
        await interaction.response.edit_message(embed=embed('Cancelled'), view=None)
        self.stop()


class SearchModal(discord.ui.Modal, title='Discover something great'):
    query = discord.ui.TextInput(label='Title, artist or author', placeholder='What would you like to enjoy?', max_length=100)

    def __init__(self, bot, owner, kind):
        super().__init__(timeout=300)
        self.bot, self.owner, self.kind = bot, owner, kind

    async def on_submit(self, interaction):
        if interaction.user.id != self.owner:
            raise UserError('This form belongs to another user.')
        self.bot.guard(interaction)
        await interaction.response.defer(ephemeral=True)
        view = ItemsView(self.bot, self.owner, 'search', (self.kind, self.query.value, 1), title='Discover')
        await view.load()
        await interaction.followup.send(embeds=view.gallery(), view=view, ephemeral=True)

    async def on_error(self, interaction, error):
        await error_message(interaction, str(error) if isinstance(error, UserError) else 'Search failed. Please try again.')


class DashboardView(OwnedView):
    def __init__(self, bot, owner):
        super().__init__(bot, owner)
        self.button('📋 My requests', self.requests, row=0)
        self.button('✨ Discover', self.discover, discord.ButtonStyle.primary, row=0)
        self.button('💽 Storage', self.storage, row=0)
        self.button('🔔 Notifications', self.notifications, row=1)
        self.button('⭐ Watchlist', self.watchlist, row=1)
        self.button('↻ Refresh', self.refresh, row=1)
        self.button('🕒 Scheduled deletions', self.deletions, row=4)
        select = discord.ui.Select(placeholder='Explore the library…', options=[
            discord.SelectOption(label=f'{ICONS[k]} {label}', value=k) for k, label in
            [('movie', 'Movies'), ('tv', 'Series'), ('music', 'Music'), ('book', 'Books')]], row=2)
        async def browse(interaction):
            await self.show_items(interaction, 'library', (select.values[0],), 'Available library')
        select.callback = browse
        self.add_item(select)
        search = discord.ui.Select(placeholder='Search & make a new request…', options=[
            discord.SelectOption(label=f'{ICONS[k]} {label}', value=k) for k, label in
            [('movie', 'Movies'), ('tv', 'Series'), ('music', 'Albums'), ('book', 'Books')]], row=3)
        async def discover(interaction):
            await interaction.response.send_modal(SearchModal(bot, owner, search.values[0]))
        search.callback = discover
        self.add_item(search)

    async def show_items(self, interaction, operation, args, title):
        await interaction.response.defer(ephemeral=True)
        view = ItemsView(self.bot, self.owner, operation, args, title)
        await view.load()
        await interaction.edit_original_response(embeds=view.gallery(), view=view)

    async def discover(self, interaction):
        await self.show_items(interaction, 'discover', ('movie', 1), 'Discover · Popular movies')

    async def requests(self, interaction):
        await self.show_items(interaction, 'requests', (False,), 'My requests')

    async def all_requests(self, interaction):
        await self.show_items(interaction, 'requests', (True,), 'All requests')

    async def watchlist(self, interaction):
        await self.show_items(interaction, 'watches', (), 'My watchlist')

    async def deletions(self, interaction):
        await self.show_items(interaction, 'deletions', (), 'My file deletions · 24-hour undo window')

    async def storage(self, interaction):
        await interaction.response.defer(ephemeral=True)
        disks = await self.bot.service.run(self.owner, 'storage')
        result = embed('💽 Storage overview', 'Live free space reported by each configured service. Shared volumes may appear more than once.')
        for source, rows in disks.items():
            for disk in rows:
                free, total = int(disk.get('freeSpace') or 0), int(disk.get('totalSpace') or 0)
                ratio = min(1, max(0, 1 - free / total)) if total else 0
                filled = round(ratio * 12)
                result.add_field(name=f"{source.title()} · {clean(disk.get('path', 'Volume'), 80)}",
                    value=f"{'▰' * filled}{'▱' * (12 - filled)}  **{ratio:.0%} used**\n**{bytes_label(free)} free** / {bytes_label(total)}", inline=False)
                if len(result.fields) == 24:
                    break
            if len(result.fields) == 24:
                break
        if not result.fields:
            result.description = 'Configure RADARR, SONARR, LIDARR or READARR URL/API key to report live storage.'
        await interaction.edit_original_response(embed=result, view=DashboardView(self.bot, self.owner))

    async def notifications(self, interaction):
        await interaction.response.defer(ephemeral=True)
        await self.bot.service.verified_account(self.owner)
        devices = await self.bot.service.offload(self.bot.service.devices)
        view = PreferencesView(self.bot, self.owner, devices)
        await interaction.edit_original_response(embed=view.render(), view=view)

    async def refresh(self, interaction):
        await interaction.response.defer(ephemeral=True)
        result = await self.bot.service.run(self.owner, 'dashboard')
        await interaction.edit_original_response(embed=dashboard_embed(result), view=DashboardView(self.bot, self.owner))


def dashboard_embed(data):
    account = data['account']
    result = embed(f"Hi, {clean(account['name'], 70)}")
    result.add_field(name='📋 Your requests', value=f"**{data['requests']}** tracked", inline=True)
    result.add_field(name='✨ Available library', value=f"**{data['library']}** items", inline=True)
    result.add_field(name='🔔 Personal updates', value='Opted in' if account['opted_in'] else 'Off · opt in below', inline=True)
    timestamp = int(float(data['meta'].get('last_success', time.time())))
    result.add_field(name='🟢 Verified', value=f'<t:{timestamp}:R>', inline=False)
    return result


class ItemsView(OwnedView):
    def __init__(self, bot, owner, operation, args=(), title='Library', query=''):
        super().__init__(bot, owner)
        self.operation, self.args, self.title = operation, args, title
        self.query = query
        self.items, self.page = [], 0
        self.catalog = []

    async def load(self):
        if self.operation == 'deletions':
            self.items = await self.bot.service.deletions(self.owner)
        elif self.operation == 'watches':
            self.items = await self.bot.service.watches(self.owner)
        else:
            self.items = await self.bot.service.run(self.owner, self.operation, *self.args)
        self.catalog = self.items
        self.filter_items()

    def filter_items(self):
        if not isinstance(self.query, str) or len(self.query) > 100:
            raise UserError('Filter must be at most 100 characters.')
        self.items = [i for i in self.catalog if self.query.lower() in i['title'].lower()]
        self.page = min(self.page, max(0, math.ceil(len(self.items) / 5) - 1))
        self.rebuild()

    def visible(self):
        return self.items[self.page * 5:self.page * 5 + 5]

    def render(self):
        pages = max(1, math.ceil(len(self.items) / 5))
        result = embed(self.title, f'**{len(self.items)} items** · {self.page + 1}/{pages}')
        for item in self.visible():
            subtitle = clean(item.get('subtitle') or ('Available' if item.get('available') else 'Not yet available'), 300)
            if item.get('size'):
                subtitle += f" · {bytes_label(item['size'])}"
            if self.operation == 'deletions' and item.get('execute_after'):
                subtitle += f"\nScheduled: <t:{int(item['execute_after'])}:F>"
            result.add_field(name=f"{ICONS.get(item['kind'], '✨')} {clean(item['title'], 180)}", value=subtitle, inline=False)
        if not self.items:
            result.description = 'No items'
        if self.operation == 'search' and self.args[0] in ('movie', 'tv'):
            result.set_footer(text=f'Search result page {self.args[2]} · /search page: lets you browse more results')
        return result

    def gallery(self):
        """One compact poster card per item, after load/access checks—not a wall of fields."""
        pages = max(1, math.ceil(len(self.items) / 5))
        header = embed(self.title, f'**{len(self.items)} titles** · Page {self.page + 1}/{pages}\nSelect a title for details and actions.')
        header.set_footer(text='Open gallery snapshot · Refresh for latest state · Actions recheck live access')
        if not self.items:
            header.description = 'No matching titles. Change the filter or search for something new.'
        if self.operation in ('search', 'discover'):
            header.description += f'\nResult batch {self.args[-1]}'
        cards = [header]
        for index, item in enumerate(self.visible(), 1):
            description = clean(item.get('subtitle') or ('Available' if item.get('available') else 'Not yet available'), 180)
            if item.get('is4k'):
                description += ' · 4K'
            if item.get('size'):
                description += f" · {bytes_label(item['size'])}"
            card = embed(f"{index}. {ICONS.get(item['kind'], '✨')} {clean(item['title'], 120)}", description)
            poster = poster_url(item)
            if poster:
                card.set_thumbnail(url=poster)
            if item.get('overview'):
                card.add_field(name='About', value=clean(item['overview'], 220), inline=False)
            if self.operation == 'deletions' and item.get('execute_after'):
                card.add_field(name='Scheduled', value=f"<t:{int(item['execute_after'])}:R>")
            cards.append(card)
        return cards

    def remote_page(self):
        return self.operation in ('search', 'discover') and self.args[0] in ('movie', 'tv')

    async def validate(self):
        await self.bot.service.offload(self.bot.service.validate_browse, self.owner,
            self.operation == 'requests' and bool(self.args[0]), self.operation in ('requests', 'library'))

    def rebuild(self):
        self.clear_items()
        if self.visible():
            select = discord.ui.Select(placeholder='Choose an item…', options=[
                 discord.SelectOption(label=f'{index + 1}. {it["title"]}'[:100], value=str(index), description=(it.get('subtitle') or it['kind'])[:100])
                for index, it in enumerate(self.visible())], row=0)
            async def details(interaction):
                selected = self.visible()[int(select.values[0])]
                await interaction.response.defer(ephemeral=True)
                await self.validate()
                fresh = selected if self.operation == 'deletions' else await self.bot.service.run(self.owner, 'details', selected)
                view = DetailView(self.bot, self.owner, fresh, self)
                await interaction.edit_original_response(embed=view.render(), view=view)
            select.callback = details
            self.add_item(select)
        self.button('← Previous', self.previous, row=1, disabled=self.page == 0 and not (self.remote_page() and self.args[-1] > 1))
        self.button('Next →', self.next, row=1, disabled=(self.page + 1) * 5 >= len(self.items) and not (self.remote_page() and self.args[-1] < 500 and self.items))
        self.button('↻ Refresh', self.refresh, row=1)
        self.button('⌂ Home', self.home, row=1)
        self.button('Filter titles', self.filter, row=2)
        self.button('Search new titles', self.search, row=2)
        if self.operation == 'requests':
            self.button('My requests' if self.args[0] else 'All requests', self.toggle_requests, row=2)
        if self.operation in ('library', 'discover'):
            kinds = [('movie', 'Movies'), ('tv', 'Series')]
            if self.operation == 'library':
                kinds = [('all', 'Everything')] + kinds + [('music', 'Music'), ('book', 'Books')]
            category = discord.ui.Select(placeholder='Change category…', options=[
                discord.SelectOption(label=label, value=kind, default=self.args[0] == kind) for kind, label in kinds], row=3)
            async def change(interaction):
                self.args = (category.values[0], 1) if self.operation == 'discover' else (category.values[0],)
                self.page = 0
                await self.refresh(interaction)
            category.callback = change
            self.add_item(category)

    async def previous(self, interaction):
        if self.page == 0 and self.remote_page() and self.args[-1] > 1:
            self.args = (*self.args[:-1], self.args[-1] - 1)
            await interaction.response.defer(ephemeral=True)
            await self.load()
            self.page = max(0, math.ceil(len(self.items) / 5) - 1)
            self.rebuild()
            await interaction.edit_original_response(embeds=self.gallery(), view=self)
            return
        self.page = max(0, self.page - 1)
        await self.show_page(interaction)

    async def next(self, interaction):
        if (self.page + 1) * 5 >= len(self.items) and self.remote_page() and self.args[-1] < 500:
            self.args = (*self.args[:-1], self.args[-1] + 1)
            self.page = 0
            await self.refresh(interaction)
            return
        self.page += 1
        await self.show_page(interaction)

    async def show_page(self, interaction):
        await interaction.response.defer(ephemeral=True)
        await self.validate()
        self.page = min(max(0, self.page), max(0, math.ceil(len(self.items) / 5) - 1))
        self.rebuild()
        await interaction.edit_original_response(embeds=self.gallery(), view=self)

    async def filter(self, interaction):
        await interaction.response.send_modal(CollectionFilterModal(self))

    async def search(self, interaction):
        kind = self.args[0] if self.operation in ('search', 'discover', 'library') and self.args[0] != 'all' else 'movie'
        await interaction.response.send_modal(SearchModal(self.bot, self.owner, kind))

    async def toggle_requests(self, interaction):
        self.args, self.page = (not self.args[0],), 0
        self.title = 'All requests' if self.args[0] else 'My requests'
        await self.refresh(interaction)

    async def refresh(self, interaction):
        await interaction.response.defer(ephemeral=True)
        await self.load()
        await interaction.edit_original_response(embeds=self.gallery(), view=self)

    async def home(self, interaction):
        await DashboardView(self.bot, self.owner).refresh(interaction)


def identity(item):
    return item.get('id'), item['kind'], item['external_id'], item.get('source')


class CollectionFilterModal(discord.ui.Modal, title='Filter this gallery'):
    def __init__(self, parent):
        super().__init__(timeout=300)
        self.parent = parent
        self.query = discord.ui.TextInput(label='Title contains', default=parent.query, required=False, max_length=100)
        self.add_item(self.query)

    async def on_submit(self, interaction):
        if interaction.user.id != self.parent.owner:
            raise UserError('This gallery belongs to another user.')
        self.parent.bot.guard(interaction)
        await interaction.response.defer(ephemeral=True)
        await self.parent.validate()
        self.parent.query, self.parent.page = self.query.value, 0
        self.parent.filter_items()
        await interaction.edit_original_response(embeds=self.parent.gallery(), view=self.parent)

    async def on_error(self, interaction, error):
        await error_message(interaction, str(error) if isinstance(error, UserError) else 'Filter unavailable. Refresh the gallery.')


class DetailView(OwnedView):
    def __init__(self, bot, owner, item, parent):
        super().__init__(bot, owner)
        self.item, self.parent = item, parent
        if parent.operation in ('search', 'discover'):
            self.button('＋ Request', self.request, discord.ButtonStyle.primary, row=0, disabled=not item.get('requestable', not item.get('available', False)))
        if parent.operation != 'deletions':
            self.button('☆ Unfollow' if parent.operation == 'watches' else '⭐ Follow', self.follow, row=0)
            if item['kind'] in ('movie', 'tv') and item.get('available'):
                self.button('▶ Open in Jellyfin', self.open, row=0)
        if parent.operation == 'requests' and item.get('available') and item.get('deletable'):
            self.button('Request file deletion', self.delete, discord.ButtonStyle.danger, row=1)
        if parent.operation == 'deletions' and item['status'] == 'pending':
            self.button('Undo file deletion', self.undo, discord.ButtonStyle.primary, row=0)
        self.button('← Back', self.back, row=1)
        self.button('⌂ Home', self.home, row=1)

    def render(self):
        item = self.item
        result = embed(f"{ICONS.get(item['kind'], '✨')} {clean(item['title'], 180)}", clean(item.get('overview'), 400) if item.get('overview') else None)
        result.add_field(name='Status', value=clean(item.get('subtitle') or ('Available' if item.get('available') else 'Not yet available'), 400))
        result.add_field(name='Category', value=item['kind'].title())
        poster = poster_url(item, 'w342')
        if poster:
            result.set_image(url=poster)
        if self.parent.operation == 'requests':
            result.add_field(name='Request', value=f"#{item['id']} · {'4K' if item.get('is4k') else 'Standard'}", inline=False)
        if self.parent.operation == 'deletions':
            result.add_field(name='24-hour undo window', value=f"Scheduled for <t:{int(item['execute_after'])}:F>. Undo before execution begins. The entire movie/series is removed from the service, including shared files. Seerr request history stays intact.", inline=False)
            if item.get('error'):
                result.add_field(name='Execution note', value=clean(item['error'], 400), inline=False)
        return result

    async def request(self, interaction):
        await interaction.response.defer(ephemeral=True)
        await self.bot.service.verified_account(self.owner)
        view = ConfirmView(self.bot, self.owner, 'request', (self.item,), f"Request {clean(self.item['title'], 100)}?", 'TV requests include all seasons. Music/book requests are sent directly to the configured service using its default profiles.')
        await interaction.followup.send(embed=view.render(), view=view, ephemeral=True)

    async def follow(self, interaction):
        await interaction.response.defer(ephemeral=True)
        if self.parent.operation == 'watches':
            message = await self.bot.service.unwatch(self.owner, self.item)
        else:
            message = await self.bot.service.run(self.owner, 'watch', self.item, False)
        await interaction.followup.send(message, ephemeral=True)

    async def open(self, interaction):
        await interaction.response.defer(ephemeral=True)
        url = await self.bot.service.run(self.owner, 'open', self.item)
        view = discord.ui.View()
        view.add_item(discord.ui.Button(label='▶ Open in Jellyfin', url=url))
        await interaction.followup.send('Open this item on your selected Jellyfin server/device. You may need to sign in there.', view=view, ephemeral=True)

    async def delete(self, interaction):
        await interaction.response.defer(ephemeral=True)
        await self.bot.service.verified_account(self.owner)
        view = ConfirmView(self.bot, self.owner, 'delete', (self.item['id'],), 'Schedule file deletion in 24 hours?', 'No files are deleted now. You can undo with /deletions during the 24-hour delay. After that, the entire movie/series is removed from Radarr/Sonarr, including files shared by other users. Seerr requests will not be deleted.')
        await interaction.followup.send(embed=view.render(), view=view, ephemeral=True)

    async def undo(self, interaction):
        await interaction.response.defer(ephemeral=True)
        message = await self.bot.service.cancel_deletion(self.owner, self.item['id'])
        await interaction.edit_original_response(embed=embed('Deletion undone', message), view=None)

    async def back(self, interaction):
        await self.parent.show_page(interaction)

    async def home(self, interaction):
        await self.parent.home(interaction)


class ConfirmView(OwnedView):
    def __init__(self, bot, owner, operation, args, title, description):
        super().__init__(bot, owner, timeout=60)
        self.operation, self.args, self.title, self.description = operation, args, title, description
        self.claimed = False
        self.button('Confirm', self.confirm, discord.ButtonStyle.danger if operation == 'delete' else discord.ButtonStyle.primary)
        self.button('Cancel', self.cancel)

    def render(self):
        return embed(self.title, self.description)

    async def confirm(self, interaction):
        if self.claimed:
            raise UserError('This confirmation was already used.')
        self.claimed = True
        await interaction.response.defer(ephemeral=True)
        self.clear_items()
        await interaction.edit_original_response(view=self)
        message = await self.bot.service.run(self.owner, self.operation, *self.args)
        await interaction.edit_original_response(embed=embed('Action recorded', message), view=None)
        self.stop()

    async def cancel(self, interaction):
        self.claimed = True
        self.stop()
        await interaction.response.edit_message(content='Cancelled. No changes were made.', embed=None, view=None)


class PreferencesView(OwnedView):
    def __init__(self, bot, owner, devices=None):
        super().__init__(bot, owner)
        self.devices = devices or {'Browser': None, **bot.config.devices}
        self.button('🔔 Opt in to DMs', self.enable, discord.ButtonStyle.primary)
        self.button('Mute all updates', self.disable)
        self.button('Sync watchlists now', self.import_watchlist)
        self.button('⌂ Home', self.home)
        if self.devices:
            select = discord.ui.Select(placeholder='Choose your Jellyfin device/server link…', options=[
                discord.SelectOption(label=name, value=name) for name in self.devices], row=1)
            async def device(interaction):
                await interaction.response.defer(ephemeral=True)
                message = await bot.service.run(owner, 'preferences', None, select.values[0])
                await interaction.edit_original_response(embed=self.render(), view=self)
            select.callback = device
            self.add_item(select)

    def render(self):
        account = self.bot.service.account(self.owner)
        result = embed('Preferences', 'DMs for requests and watched items. Off by default.')
        result.add_field(name='Personal DMs', value='🟢 Enabled' if account['opted_in'] else '⚪ Muted')
        result.add_field(name='Jellyfin link', value=clean(account.get('device') or next(iter(self.devices))))
        result.add_field(name='Watchlists', value='Movies/series sync with Seerr automatically, including removals. Music/books stay hub-only. Watching does not request media.', inline=False)
        return result

    async def set_opt_in(self, interaction, enabled):
        await interaction.response.defer(ephemeral=True)
        if enabled:
            await self.bot.service.run(self.owner, 'preferences', True)
        else:
            self.bot.service.mute_notifications(self.owner)
            await interaction.edit_original_response(embed=embed('Updates muted', 'All personal updates muted.'), view=None)
            return  # Generic local safety receipt; never re-render cached account/device details.
        await interaction.edit_original_response(embed=self.render(), view=self)

    async def enable(self, interaction):
        await self.set_opt_in(interaction, True)

    async def disable(self, interaction):
        await self.set_opt_in(interaction, False)

    async def import_watchlist(self, interaction):
        await interaction.response.defer(ephemeral=True)
        message = await self.bot.service.run(self.owner, 'sync_watchlist')
        await interaction.followup.send(message, ephemeral=True)

    async def home(self, interaction):
        await DashboardView(self.bot, self.owner).refresh(interaction)
