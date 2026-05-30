# Seerr Discord Bot

A Discord bot that links Jellyfin users to Seerr and lets them view/remove their media requests directly from Discord – all through private slash commands.

---

## Features

- **Secure Jellyfin linking** – users authenticate once via a private modal; credentials are never stored.
- **Persistent user mapping** – Discord ID → Seerr user saved in JSON (or easily swappable to a database).
- **Local request cache** – all Seerr requests are synced every hour into a SQLite database for instant access.
- **Paginated request viewer** – browse your requests with page buttons.
- **Full media removal** – remove the entire media entry from Seerr (and optionally from Radarr/Sonarr) with one click.
- **Ephemeral responses** – all bot messages and modals are visible only to the interacting user.
- **Hash‑based change detection** – sync only runs when something actually changed, reducing API load.
- **Exponential backoff** – gracefully retries TMDB lookups for media details if the API is slow.

---

## Prerequisites

- **Python 3.10+** (tested up to 3.14)
- **Seerr instance** (Jellyseerr works too)
- **Jellyfin server** (or Emby – just change `server_type`)
- **Discord Bot Token** – [create one here](https://discord.com/developers/applications)
- **Seerr Admin API key** – obtain from Seerr *Settings → General → API Key*
- **pip** and `git` for installation

---

## Docker Installation

1. **Create a `docker-compose.yaml` file:**

   ```yaml
   services:
   discord-bot:
      image: ghcr.io/somerandomdude-a/seerr-discord-bot:latest
      volumes:
         - ./data:/data
      environment:
         - SEERR_URL=https://movies.neblo.in/
         - SEERR_ADMIN_KEY=your-seerr-key
         - DISCORD_TOKEN=your-discord-bot-token
      restart: unless-stopped
   ```

2. **Create a folder `./data` in your installation location** 
      You can configure this by mounting your own volume in `/data` inside the container

3. run `docker compose up -d` (you may need to use `sudo` depending on your environment)

## Python Installation

1. **Clone the repository**

   ```bash
   git clone https://github.com/someRandomDude-a/seerr-discord-bot.git
   cd seerr-discord-bot
   ```

2. **Create a virtual environment (recommended)**

   ```bash
   python -m venv venv
   source venv/bin/activate      # Linux/macOS
   venv\Scripts\activate         # Windows
   ```

3. **Install dependencies**

   ```bash
   pip install uv
   uv pip install -r requirements.txt
   ```

4. **Copy the environment template**

   ```bash
   cp .env.example .env
   ```

   Then fill in the `.env` file with your actual data (see [Configuration](#configuration)).

---

## Configuration

The bot uses a `.env` file for all settings. Example:

```ini
SEERR_URL=http://localhost:5055
SEERR_ADMIN_KEY=your-seerr-admin-api-key
DISCORD_TOKEN=your-discord-bot-token
DATABASE_PATH=seerr_cache.db   # optional, default is "seerr_cache.db"
```

| Variable | Required | Description |
| ---------- | ---------- | ------------- |
| `SEERR_URL` | Yes | Full URL of your Seerr instance (e.g., `http://192.168.1.100:5055`) |
| `SEERR_ADMIN_KEY` | Yes | Admin API key from Seerr *Settings → General* |
| `DISCORD_TOKEN` | Yes | Discord bot token (from [Discord Developer Portal](https://discord.com/developers/applications)) |
| `DATABASE_PATH` | No | Path to the SQLite cache database (default: `seerr_cache.db`) |

---

## Running the Bot

Start the bot with:

```bash
python bot.py
```

### The bot will

- Synchronise all Seerr requests into a local SQLite database (first run takes longer; subsequent syncs are instant if no changes).
- Start a background sync every hour.
- Log in to Discord and register the slash commands `/link` and `/requests`.

---

## Usage (Slash Commands)

All commands are private (ephemeral) – only you can see the responses.

### `/link`

#### Linking your Jellyfin account for the first time

1. Type `/link` in any channel the bot can see.
2. A **modal** will appear (only visible to you) asking for your Jellyfin username and password.
3. On success, your Discord ID is permanently linked to your Seerr/Jellyfin user.
4. You only need to do this once – future uses of `/requests` will automatically identify you.

### `/requests`

#### View and manage your Seerr media requests

- Shows a paginated list of all your requests (sorted by last update).
- Each request has a **Remove** button.
- Clicking **Remove** will:
  1. Delete the media from Seerr (`DELETE /api/v1/media/{media_id}`).
  2. (If configured in Seerr’s settings) also delete it from Radarr/Sonarr.
  3. Remove the request from your list instantly.
- The list updates live after removal, and you can navigate pages with ◀ ▶ buttons.

---

## How It Works

### 1. Sync Engine (`sync.py`)

- Fetches **all** Seerr requests paginated every hour.
- Computes a SHA‑256 hash of the sorted list; if unchanged, skips syncing.
- Stores requests in `requests` table (SQLite), enriched with TMDB metadata (title, poster, etc.) in `media_details`.
- Uses a single database connection per cycle to avoid locks, with WAL mode enabled.
- Exponential backoff (up to 5 retries) when fetching media details from TMDB.

### 2. User Linking

- Discord user IDs are mapped to Seerr user IDs in `linked_users.json`.
- The `/link` modal authenticates against Jellyfin via Seerr’s `POST /auth/jellyfin` using a temporary client – no API key or password storage.

### 3. Request Removal

- The **Remove** button calls Seerr’s `DELETE /api/v1/media/{media_id}` endpoint.
- This removes the entire media record from Seerr (and optionally files if `DELETE /media/{media_id}/file` is enabled).
- To also delete from Radarr/Sonarr, ensure **Media Deletion** is turned on in Seerr’s *Settings → Services*.

### 4. Privacy & Security

- The Discord token, Seerr admin key, and all credentials stay in `.env`.
- User passwords are discarded immediately after verification.
- All bot replies are ephemeral (visible only to the command caller).

---

## FAQ

**Can I approve/decline requests from the bot?**  
Not yet – this version focuses on removal. Adding `/approve` and `/decline` is trivial and can be added on request.

**Will my Jellyfin password be exposed?**  
No. The modal is temporary and the password is used only once to authenticate against Seerr. It is never stored, logged, or transmitted outside that single API call.

**Does the bot support Emby or Plex?**  
Currently only Jellyfin (due to `login_with_jellyfin` with `server_type=0`). To use Emby, change `server_type=1`. Plex would require a token, which isn’t currently implemented in the modal.

**Are request IDs ever reused?**  
No. Seerr uses auto‑increment IDs that are never recycled, so your local database remains consistent.

---

## Project Structure

```dir
.
├── bot.py               # Discord bot with slash commands & UI
├── seerr/               # Seerr API Python client (your package)
│   ├── __init__.py
│   ├── client.py
│   ├── sync.py
│   └── exceptions.py
├── .env                 # Your secrets (not committed)
├── .env.example         # Template for .env
├── linked_users.json    # Discord↔Seerr user mapping (auto-created)
├── seerr_cache.db       # Local request database (auto-created)
└── README.md
```

---

## License

This project is open‑source under the [MIT License](LICENSE). Feel free to use, modify, and distribute it.

---

## Acknowledgements

- Built on top of [Seerr](https://github.com/seerr-team/seerr) API.
- Uses [discord.py](https://github.com/Rapptz/discord.py) for the bot framework.
- Icons & inspiration from the Jellyfin/Emby community.

---
