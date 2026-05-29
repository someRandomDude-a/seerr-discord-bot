import os
import json
import sqlite3
import discord
from discord.ext import commands
from dotenv import load_dotenv
from seerr import SeerrAPI
from seerr import SyncManager

load_dotenv()
SEERR_URL = os.getenv("SEERR_URL", "")
SEERR_ADMIN_KEY = os.getenv("SEERR_ADMIN_KEY", "")
DISCORD_TOKEN = os.getenv("DISCORD_TOKEN", "")
DB_PATH = os.getenv("DATABASE_PATH", "seerr_cache.db")
LINKED_FILE = "linked_users.json"

#  Seerr API & Sync Manager
api = SeerrAPI(SEERR_URL, api_key=SEERR_ADMIN_KEY)
sync_mgr = SyncManager(api, DB_PATH)
sync_mgr.start_loop(interval=3600)   # sync every hour

# User Link Storage (JSON)
def load_links() -> dict:
    """Load Discord ID → Seerr user mapping from JSON file."""
    try:
        with open(LINKED_FILE, "r") as f:
            return json.load(f)
    except (FileNotFoundError, json.JSONDecodeError):
        return {}

def save_links(links: dict):
    """Save links dictionary to JSON file."""
    with open(LINKED_FILE, "w") as f:
        json.dump(links, f, indent=2)

def get_seerr_user(discord_id: int) -> dict | None:
    """Return the linked Seerr user data for a Discord user, or None."""
    links = load_links()
    return links.get(str(discord_id))

# Status mapping for display
STATUS_MAP = {
    1: "Pending",
    2: "Approved",
    3: "Declined",
    5: "Available",
    6: "Partial"
}

# Login Modal
class JellyfinLoginModal(discord.ui.Modal, title="Link your Jellyfin Account"):
    username = discord.ui.TextInput(
        label="Jellyfin Username",
        placeholder="Enter your Jellyfin username",
        required=True,
        max_length=100
    )
    password = discord.ui.TextInput(
        label="Jellyfin Password",
        placeholder="Enter your Jellyfin password",
        required=True,
        style=discord.TextStyle.short,
        max_length=128
    )

    def __init__(self, seerr_url: str):
        super().__init__()
        self.api_url = seerr_url

    async def on_submit(self, interaction: discord.Interaction):
        # Verify with Seerr using a temporary client
        temp_api = SeerrAPI(self.api_url)
        try:
            temp_api.login_with_jellyfin(
                username=self.username.value,
                password=self.password.value,
                server_type=0  # 0 = Jellyfin, 1 = Emby
            )
            user = temp_api.get_current_user()
        except Exception:
            await interaction.response.send_message(
                "❌ Invalid Jellyfin credentials. Please try again.",
                ephemeral=True
            )
            return

        # confirm the username matches
        if user["displayName"].lower() != self.username.value.lower():
            await interaction.response.send_message(
                "❌ Authenticated user does not match the provided username.",
                ephemeral=True
            )
            return

        # Save the link
        links = load_links()
        links[str(interaction.user.id)] = {
            "seerr_id": user["id"],
            "username": user["displayName"]
        }
        save_links(links)

        await interaction.response.send_message(
            f"✅ Your Jellyfin account **{user['displayName']}** has been linked!",
            ephemeral=True
        )

# Paginated Requests View with Remove Buttons
class RequestsView(discord.ui.View):
    def __init__(self, user_id: int, requests_data: list, page_size: int = 5):
        super().__init__(timeout=300)
        self.user_id = user_id
        self.requests = requests_data
        self.page_size = page_size
        self.current_page = 0
        self.total_pages = max(1, -(-len(requests_data) // page_size))
        self._update_buttons()

    def _get_page_requests(self):
        start = self.current_page * self.page_size
        end = start + self.page_size
        return self.requests[start:end]

    def _build_embed(self):
        page_reqs = self._get_page_requests()
        embed = discord.Embed(
            title="Your Requests",
            color=0x00ff00,
            description=f"Page {self.current_page + 1}/{self.total_pages}"
        )
        for req in page_reqs:
            status = STATUS_MAP.get(req["status"], "Unknown")
            embed.add_field(
                name=f"{req['title']} ({req['type']})",
                value=f"Status: **{status}** | ID: {req['id']}",
                inline=False
            )
        if not page_reqs:
            embed.description = "No requests found."
        return embed

    def _update_buttons(self):
        self.clear_items()

        # Remove buttons for each request on this page
        for req in self._get_page_requests():
            btn = discord.ui.Button(
                label=f"Remove {req['title'][:20]}",
                style=discord.ButtonStyle.danger,
                custom_id=f"remove_{req['id']}"
            )
            # Pass media_id to the callback to delete the whole media
            btn.callback = self._make_remove_callback(req["id"], req["media_id"])
            self.add_item(btn)

        # Pagination buttons
        if self.total_pages > 1:
            prev_btn = discord.ui.Button(
                label="◀ Previous",
                style=discord.ButtonStyle.primary,
                custom_id="prev",
                disabled=(self.current_page == 0)
            )
            next_btn = discord.ui.Button(
                label="Next ▶",
                style=discord.ButtonStyle.primary,
                custom_id="next",
                disabled=(self.current_page == self.total_pages - 1)
            )
            prev_btn.callback = self.prev_page
            next_btn.callback = self.next_page
            self.add_item(prev_btn)
            self.add_item(next_btn)

    def _make_remove_callback(self, request_id: int, media_id: int):
        async def callback(interaction: discord.Interaction):
            try:
                api._request('DELETE', f'/media/{media_id}/file')
            except Exception:
                pass  # Continue even if file deletion fails

            # Delete the media entry (which removes all requests and triggers *arr deletion)
            try:
                api._request('DELETE', f'/media/{media_id}')
            except Exception as e:
                await interaction.response.send_message(
                    f"❌ Failed to delete media: {e}",
                    ephemeral=True
                )
                return

            # Remove all requests with this media_id from the local cache
            self.requests = [r for r in self.requests if r["media_id"] != media_id]
            self.total_pages = max(1, -(-len(self.requests) // self.page_size))
            if self.current_page >= self.total_pages:
                self.current_page = self.total_pages - 1
            self._update_buttons()
            await interaction.response.edit_message(embed=self._build_embed(), view=self)
        return callback

    async def prev_page(self, interaction: discord.Interaction):
        self.current_page -= 1
        self._update_buttons()
        await interaction.response.edit_message(embed=self._build_embed(), view=self)

    async def next_page(self, interaction: discord.Interaction):
        self.current_page += 1
        self._update_buttons()
        await interaction.response.edit_message(embed=self._build_embed(), view=self)

# Bot Setup
class SeerrBot(commands.Bot):
    def __init__(self):
        intents = discord.Intents.default()
        intents.message_content = True
        super().__init__(command_prefix="!", intents=intents)

    async def setup_hook(self):
        await self.tree.sync()   # sync slash commands globally

bot = SeerrBot()

# Slash Commands
@bot.tree.command(name="link", description="Link your Jellyfin account to the bot")
async def link_cmd(interaction: discord.Interaction):
    # Already linked check
    linked = get_seerr_user(interaction.user.id)
    if linked:
        await interaction.response.send_message(
            f"Your account is already linked as **{linked['username']}**. Use `/requests` to view your requests.",
            ephemeral=True
        )
        return

    # Show the login modal
    modal = JellyfinLoginModal(SEERR_URL)
    await interaction.response.send_modal(modal)

@bot.tree.command(name="requests", description="View and manage your Seerr requests")
async def requests_cmd(interaction: discord.Interaction):
    linked = get_seerr_user(interaction.user.id)
    if not linked:
        await interaction.response.send_message(
            "You haven't linked your Jellyfin account yet. Use `/link` to get started.",
            ephemeral=True
        )
        return

    # Fetch requests from local SQLite cache
    with sqlite3.connect(DB_PATH) as conn:
        conn.execute("PRAGMA journal_mode=WAL")
        rows = conn.execute("""
            SELECT id, title, type, status, poster_path, media_id
            FROM requests
            WHERE requested_by_id = ?
            ORDER BY updated_at DESC
        """, (linked["seerr_id"],)).fetchall()

    requests_list = []
    for row in rows:
        requests_list.append({
            "id": row[0],
            "title": row[1],
            "type": row[2],
            "status": row[3],
            "poster_path": row[4],
            "media_id": row[5]
        })

    if not requests_list:
        await interaction.response.send_message("You have no requests.", ephemeral=True)
        return

    view = RequestsView(interaction.user.id, requests_list)
    embed = view._build_embed()
    await interaction.response.send_message(embed=embed, view=view, ephemeral=True)

# Run
if __name__ == "__main__":
    bot.run(DISCORD_TOKEN)