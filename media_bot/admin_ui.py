"""Private slash-command equivalents of the Activity's operator tools."""
import math
import discord

from .security import UserError
from .ui import OwnedView, AnnouncementConfirmView, clean, embed


class AdminView(OwnedView):
    # OwnedView checks identity/entry limits immediately. Each data load/send rechecks
    # live authority after deferring. Reply may open a modal without an upstream call;
    # submission verifies authority before creating any preview. This avoids missing
    # Discord's 3-second acknowledgement deadline during a slow Seerr request.
    pass


class AdminListView(AdminView):
    def __init__(self, bot, owner, mode, filters=None, page=1, job=None):
        super().__init__(bot, owner)
        self.mode, self.filters, self.page, self.job = mode, filters or {}, page, job
        self.rows, self.total = [], 0

    async def load(self):
        if self.mode == 'inbox':
            from functools import partial
            data = await self.bot.service.offload(partial(self.bot.admin.inbox, self.owner, **self.filters, page=self.page, page_size=5))
            self.rows, self.total = data['messages'], data['total']
        elif self.mode == 'deliveries' and self.job:
            data = await self.bot.service.offload(self.bot.admin.deliveries, self.owner, self.job, self.page)
            self.rows, self.total = data['rows'], data['total']
        else:
            state = await self.bot.service.offload(self.bot.admin.state, self.owner)
            rows = state['guilds'] if self.mode == 'servers' else state['jobs']
            self.total = len(rows)
            self.rows = rows[(self.page - 1) * 5:self.page * 5]
        last_page = max(1, math.ceil(self.total / 5))
        if self.page > last_page:
            self.page = last_page
            return await self.load()
        self.clear_items()
        if self.mode == 'inbox' and self.rows:
            choice = discord.ui.Select(placeholder='Read / reply…', options=[
                discord.SelectOption(label=(r['author_name'] or r['author_id'])[:100], value=r['message_id'],
                    description=(r['guild_name'] or 'DM')[:100]) for r in self.rows], row=0)
            async def detail(interaction):
                selected = interaction.data['values'][0]
                await self.defer_update(interaction)
                # Re-read retained messages with the same filters, not a stale UI snapshot.
                fresh = self.copy()
                await fresh.load()
                row = next((r for r in fresh.rows if r['message_id'] == selected), None)
                if not row:
                    raise UserError('Message moved or expired. Refresh the inbox.')
                view = InboxDetailView(self.bot, self.owner, row, fresh)
                fresh.flow = self.flow
                await self.update(interaction, embed=view.render(), view=view)
            choice.callback = self.callback(detail)
            self.add_item(choice)
        if self.mode == 'deliveries' and not self.job and self.rows:
            choice = discord.ui.Select(placeholder='Choose a delivery job…', options=[
                discord.SelectOption(label=f"Job #{r['id']}", value=str(r['id'])) for r in self.rows], row=0)
            async def delivery(interaction):
                await self.defer_update(interaction)
                view = AdminListView(self.bot, self.owner, 'deliveries', job=int(interaction.data['values'][0]))
                await view.load()
                await self.update(interaction, embed=view.render(), view=view)
            choice.callback = self.callback(delivery)
            self.add_item(choice)
        self.button('←', self.previous, row=1, disabled=self.page <= 1)
        self.button('→', self.next, row=1, disabled=self.page * 5 >= self.total)
        self.button('Refresh', self.refresh, row=1)

    def render(self):
        title = {'inbox': 'Inbox', 'servers': 'Servers', 'deliveries': 'Deliveries'}[self.mode]
        result = embed(title, f'{self.page}/{max(1, math.ceil(self.total / 5))} · {self.total} items')
        result.set_footer(text='Operator only · /inbox filters · /deliveries job:ID')
        for r in self.rows:
            if self.mode == 'inbox':
                source = f"{r['guild_name']} / #{r['channel_name']}" if r['guild_id'] else 'DM'
                value = f"{clean(source, 140)} · <t:{int(r['created_at'])}:R>\n{clean(r['content'] or 'No text', 550)}"
                name = clean(r['author_name'], 120)
            elif self.mode == 'servers':
                name = clean(r['name'], 120)
                value = f"`{r['id']}` · {clean(r['channel'] or 'No announcement channel', 100)}"
            elif self.job:
                name = clean(r['label'], 120)
                value = f"{r['status']} · `{r['target_id']}`\n{clean(r['error'] or '', 250)}"
            else:
                name = f"#{r['id']}"
                value = f"{r['sent']} sent · {r['pending']} pending · {r['failed']} blocked · {r['uncertain']} unconfirmed"
            result.add_field(name=name, value=value, inline=False)
        return result

    def copy(self, page=None):
        return AdminListView(self.bot, self.owner, self.mode, self.filters,
            self.page if page is None else page, self.job)

    async def refresh(self, interaction, page=None):
        await self.defer_update(interaction)
        view = self.copy(page)
        await view.load()
        await self.update(interaction, embed=view.render(), view=view)

    async def previous(self, interaction):
        await self.refresh(interaction, page=max(1, self.page - 1))

    async def next(self, interaction):
        await self.refresh(interaction, page=self.page + 1)


class InboxDetailView(AdminView):
    def __init__(self, bot, owner, message, parent):
        super().__init__(bot, owner)
        self.message, self.parent = message, parent
        self.button('Reply…', self.reply)
        self.button('Back', parent.refresh)
        if message['guild_id']:
            self.add_item(discord.ui.Button(label='Open in Discord', url=f"https://discord.com/channels/{message['guild_id']}/{message['channel_id']}/{message['message_id']}"))

    def render(self):
        r = self.message
        result = embed(clean(r['author_name'], 120), clean(r['content'] or 'No text', 4000))
        result.set_footer(text='Operator only · Retained message')
        result.add_field(name='Source', value=clean(r['guild_name'] or 'DM', 120))
        result.add_field(name='Sender', value=r['author_id'])
        if r['attachment_count']:
            result.add_field(name='Attachments', value=str(r['attachment_count']))
        return result

    async def reply(self, interaction):
        await interaction.response.send_modal(InboxReplyModal(self.bot, self.owner, self.message, self))


class InboxReplyModal(discord.ui.Modal, title='Reply'):
    message = discord.ui.TextInput(label='Message', style=discord.TextStyle.paragraph, max_length=2000)

    def __init__(self, bot, owner, source, parent=None):
        super().__init__(timeout=120)
        self.bot, self.owner, self.source = bot, owner, source
        self.parent = parent
        self.revision = parent.revision if parent else None

    async def on_submit(self, interaction):
        if interaction.user.id != self.owner:
            raise UserError('This reply belongs to another operator.')
        self.bot.guard(interaction)
        if self.parent:
            await self.parent.perform(interaction, self.prepare, self.revision)
        else:
            await self.prepare(interaction)

    async def prepare(self, interaction):
        if self.parent:
            await self.parent.defer_update(interaction)
        else:
            await interaction.response.defer(ephemeral=True, thinking=True)
        await self.bot.service.offload(self.bot.admin.require_admin, self.owner)
        r = self.source
        plan = await self.bot.admin.prepare(self.owner, self.message.value,
            [int(r['guild_id'])] if r['guild_id'] else [],
            user_id=None if r['guild_id'] else int(r['author_id']),
            channel_id=int(r['channel_id']) if r['guild_id'] else None)
        view = AnnouncementConfirmView(self.bot, self.owner, plan)
        if self.parent:
            await self.parent.update(interaction, embed=view.render(), view=view)
        else:
            await interaction.followup.send(embed=view.render(), view=view, ephemeral=True)

    async def on_error(self, interaction, error):
        from .ui import error_message
        await error_message(interaction, str(error) if isinstance(error, UserError) else 'Reply not confirmed.')
