# Media · Discord Activity & Bot

Browse media, requests, storage and watchlists inside Discord. A minimal **liquid-glass Activity** is available through `/activity`; private slash commands remain a fallback.

**New installation:** use the [integrated onboarding/admin panel](docs/onboarding.md). It runs on a separate private HTTP port, verifies connections, and saves configuration. The [detailed setup guide](docs/setup.md) covers Discord permissions and first-run checks. Activity hosting is optional.

## Features

- Passwordless Jellyfin **Quick Connect**, in Discord or the Activity. Verified Discord IDs are saved in Seerr’s `discordIds`; encrypted Jellyfin user tokens are checked live before private data is shown.
- Movies/TV through Seerr; albums through Lidarr; books through compatible Readarr v1 installations.
- Seerr-derived admins after verified linking, optional bot-only exceptions, and automatic import of all Seerr Radarr/Sonarr instances, including 4K.
- Local liquid-glass setup/admin panel, persisted settings, verified steps and deployment presets.
- Live dependency checks before upstream reads/actions. Snapshot reads refresh first; discovery/details query Seerr directly without refreshing unrelated services. Failures never fall back to old state.
- Poster galleries, popular-title discovery, searchable collections, quick details and navigation that preserves your place.
- **24-hour file deletion delay**, with owner-only Undo. No approval queue, and no Seerr request/media-record deletion.
- Seerr-discovered Jellyfin browser/item links, optional device destinations, opt-in DMs, automatic two-way movie/series watchlist sync and authenticated service webhooks.
- Discord OAuth, verified guild membership, ownership checks, rate limits, scoped Seerr permissions and CSRF support. Service API keys stay out of the frontend/SQLite; encrypted Jellyfin user credentials persist in SQLite.

## Start

Requires Python 3.10+, current Seerr supporting Jellyfin Quick Connect, and Quick Connect enabled in Jellyfin. Older Jellyseerr versions without these auth endpoints must be upgraded. Basic operation needs neither Message Content intent nor Administrator permission; optional admin features may require privileged intents below.

```sh
python -m pip install -r requirements.txt
python bot.py
```

Open the private panel URL printed at startup and enter its one-time console code. Choose a preset, verify Discord/Seerr, then **Save & apply**. No `.env` is required; [`.env.example`](.env.example) contains optional panel/data bootstrap settings only.

`/link` displays a private code. Approve it in an already signed-in Jellyfin client under **Settings → Quick Connect**. The bot verifies the Jellyfin identity and matching Seerr account, saves the Discord ID, and encrypts the resulting Jellyfin access token. The temporary code/cookie session is discarded. Verification persists across restarts with no arbitrary identity TTL; before private disclosures the bot checks the retained token against Jellyfin `/Users/Me`. Rejected tokens or deleted/disabled accounts require `/link` again. Outages hide private data without discarding credentials.

Unverified viewers receive only generic verification instructions—no server/device labels, operational metadata, server links, media records or images. Discord OAuth alone is not sufficient. Old links need one new `/link` because the previous implementation did not retain Jellyfin tokens. Back up and protect the database **and its sibling `jellyfin.key`**; Unix key mode is `0600` (protect Windows ACLs too). Losing the key requires users to reverify. The console-code-authenticated private operator panel remains a separate administration trust path.

## Enable the Discord Activity

### Slash-only toggle

```dotenv
ACTIVITY_ENABLED=false
WEBHOOK_SECRET=
```

Choose **Slash only** in the panel, or use the settings above. This disables the embedded dashboard and bot backend HTTP listener. The separate **private admin panel** stays available unless `PANEL_ENABLED=false`. No public hostname, tunnel, OAuth client secret, frontend build or public incoming port is needed. Discord and configured media APIs must still be reachable outbound. Keep `WEBHOOK_SECRET` only for optional webhook reception; polling works without it. Save & apply changes. If Activity hosting fails, all slash commands remain available; `/activity` cannot launch without its HTTPS backend.

| Feature | Slash-command access (private components included) |
| --- | --- |
| Connect account | `/link` |
| Overview, library, popular titles, search/request movies/TV/music/books | `/dashboard`, `/library`, `/discover`, `/search` |
| Request status and file deletion | `/requests` → select item → confirm deletion |
| Deletion history and Undo | `/deletions` → select item → Undo |
| Storage, Jellyfin links | `/storage`, item details → Open in Jellyfin |
| Watch/follow/unfollow/two-way sync | Item details, `/watchlist`, `/notifications` → Sync watchlists now |
| Opt-in/mute and device choice | `/notifications` |
| Health / Seerr link | `/status`, `/seerr` |
| Admin selected/multiple/all-server or user messages | `/announce`, `server_ids` accepts comma-separated IDs |
| Admin server destinations | `/servers` |
| Admin inbox/filter/read/reply | `/inbox` → select message → Reply → confirmation |
| Admin announcement results | `/deliveries`, `/deliveries job:ID` |

`/inbox` supports `kind`, `server_id`, `channel_id`, `user_id`, `query`, `order` and `page`. It shows five messages per page, with Next/Previous/Refresh. `/servers` lists joined guild IDs for targeting; `/announce channel_id:...` optionally overrides a channel within one selected server. Discord admin tools require Jellyfin verification, plus a live Seerr admin role or a bot-only role exception. Exceptions do not bypass identity checks. Operator-authored announcements still use explicitly confirmed destinations; URL previews are suppressed. Do not broadcast private server information to unverified audiences. Liquid-glass CSS applies to Activity/local panel; Discord controls native component appearance.

`/library`, `/requests`, `/watchlist` and `/deletions` accept an optional `query` title filter. Media-backed lists refresh before filtering; opening the watchlist reconciles with Seerr first. Watchlist/history require live Jellyfin identity verification. Owner-only Undo/mute remain available through existing controls during outages and return only generic safety receipts.

### Browsing and discovery

- **Activity:** Home shortcuts lead to Discover or Library. Discover opens with popular movies/series, not an empty search form. Click a poster/title for a live synopsis and actions; Back preserves your gallery, filter and position. Library/request/watchlist galleries show 12 cards per page, with sorting and saved title filters. Category/filter/page changes within that gallery use its labelled snapshot rather than re-syncing everything. Poster downloads are lazy, authenticated and bounded; unavailable artwork has a category fallback.
- **Slash commands:** `/discover` browses popular movies/series. Five numbered poster cards appear per gallery page. Select a title for details, use Back/Home, Filter titles, Search new titles, category selection or the My/All request toggle. Next/Previous automatically cross search-result batches. Paging/back/filtering revalidate identity and request-view permissions without refreshing every integration.
- **Freshness:** opening/refreshing a library/request/storage collection still refreshes upstream snapshots and fails closed on errors. New searches, popular feeds, item details and Jellyfin links fetch their own live dependencies and do not wait on unrelated snapshot writers. Mutations continue to check ownership, permissions, quotas and current availability before execution. An already-open gallery is explicitly a browsing snapshot, never a fallback after a failed refresh.

Discovery runs through the verified user's Seerr API context (`X-API-User`), so Seerr remains responsible for that user's permissions and request limits. **Open Seerr** is available after verification in Discover and via `/seerr`. It opens the real site with the user's own browser session; it does not transfer bot credentials or automatically sign them in. The raw Seerr UI is deliberately **not** iframe-proxied: its browser session cookies/CSRF protections and the Discord Activity origin require a separate supported SSO/session integration. A Jellyfin token is not a Seerr browser login. Do not disable cookie/CSRF/frame safeguards or expose API keys to make embedding work.

Activities are embedded web views **inside Discord**, not ordinary bot embeds. They require an HTTPS backend and Discord Developer Portal configuration.

1. Use the **same application** as the bot. Choose **Activity** in the panel and configure these fields (environment overrides remain supported):

   ```dotenv
   ACTIVITY_ENABLED=true
   DISCORD_APPLICATION_ID=your-application-id
   DISCORD_CLIENT_SECRET=your-oauth-client-secret
   BOT_HOST=0.0.0.0
   BOT_PORT=8080
   ```

2. Build the interface with Node 22+:

   ```sh
   cd activity
   npm ci
   npm run build
   ```

3. Serve the Python backend on a dedicated HTTPS hostname through a reverse proxy. In **Developer Portal → Activities**, enable Activities and map prefix `/` to that hostname.
4. Under **OAuth2 → Redirects**, add `https://127.0.0.1` as the SDK’s placeholder redirect. The embedded SDK handles the actual return to Discord.
5. Enable Developer Mode in Discord. Launch with `/activity` or the application’s **Launch** entry point in the App Launcher. Development Activities may be limited to application-team members until Discord distribution is configured.

Discord OAuth uses `identify` and `guilds`. The backend verifies identity and configured guild membership with Discord’s API, never a frontend-provided user ID. Every viewer has private data and independent preferences, even in a shared Activity. Activity session/OAuth tokens are memory-only and expire; reopen the Activity to reauthenticate. Jellyfin verification is a separate, persistent token check. Keep the OAuth client secret strictly server-side.

The Activity shows a live verification timestamp and refreshes every 60 seconds while visible. Local collection filtering does not contact upstream services; actions always revalidate. Slash commands cannot be CSS-themed. Never route the private setup/admin panel through the public Activity hostname.

## Docker

The Dockerfile builds the Activity automatically and runs Python as UID `10001`.

```sh
docker compose -f templates/compose.slash.yml up -d --build
docker compose -f templates/compose.slash.yml port media 8787
docker compose -f templates/compose.slash.yml logs media
```

Open the reported host-loopback panel port and use the console code in the logs. For Activity/webhooks use [templates/compose.activity.yml](templates/compose.activity.yml), which also publishes the bot backend on host-loopback `8080`. See [presets and secure access](docs/onboarding.md#start-with-a-preset).

Named volumes handle writable permissions; bind mounts must be writable by UID `10001`. Run **one instance per database**. Proxy HTTPS traffic only to the bot backend, never the panel. Do not share access-code logs or log Authorization headers, OAuth POST bodies, or Quick Connect secrets. With Activity/webhooks disabled, only the private admin panel listens; set `PANEL_ENABLED=false` for a listener-free headless install.

For `attempt to write a readonly database`, follow [SQLite storage permissions](docs/setup.md#sqlite-storage-permissions). Existing volumes may retain ownership from an older image; mount `/data` read-write and set `DATA_DIR=/data` explicitly. Do not delete the database or run the bot permanently as root.

## Configuration

Configure routine settings in the panel; they persist in private `DATA_DIR/settings.json`. Environment values override saved settings and appear read-only in the panel. [`.env.example`](.env.example) contains bootstrap options; [`.env.advanced.example`](.env.advanced.example) retains every advanced setting's description, units, dependencies and defaults. Never commit secrets; protect data/backups. Boolean typos and invalid Discord IDs fail validation instead of silently changing security settings.

| Variables | Purpose/default |
| --- | --- |
| `SEERR_URL`, `SEERR_ADMIN_KEY`, `DISCORD_TOKEN` | Required backend credentials |
| `ALLOWED_GUILD_IDS` | Comma-separated allowed guilds; empty allows all guilds/DMs |
| `DATA_DIR`, `DATABASE_PATH` | Persistent SQLite; `./data/seerr_cache.db` |
| `PANEL_ENABLED`, `PANEL_HOST`, `PANEL_PORT` | Private panel; `true`, `127.0.0.1`, `0` (random) |
| `SEERR_DISCOVERY`, `SEERR_ADMINS` | Import Radarr/Sonarr and infer verified admins; both `true` |
| `ADMIN_DISCORD_IDS` | Explicit bot-only operator exceptions; does not modify Seerr roles |
| `SYNC_INTERVAL_SECONDS`, `API_TIMEOUT_SECONDS` | `300` background refresh/deletion check; `15` per API call |
| `LINK_TTL_SECONDS`, `LINK_POLL_INTERVAL_SECONDS` | Quick Connect lifetime, at most `300`; poll every `5` seconds |
| `RATE_LIMIT_COUNT`, `RATE_LIMIT_WINDOW_SECONDS` | `15` user interactions per `60` seconds, shared by commands/Activity |
| `LOGIN_RATE_LIMIT_COUNT`, `LOGIN_RATE_LIMIT_WINDOW_SECONDS` | `5` connection starts per user per `600` seconds |
| `ACTIVITY_ENABLED`, `DISCORD_APPLICATION_ID`, `DISCORD_CLIENT_SECRET` | Enable the embedded dashboard and OAuth |
| `ACTIVITY_SESSION_TTL_SECONDS`, `ACTIVITY_REFRESH_INTERVAL_SECONDS` | `900` session lifetime; `60` UI refresh (minimum `30`) |
| `BOT_HOST`, `BOT_PORT` | HTTP backend bind, `127.0.0.1:8080` |
| `JELLYFIN_DEVICE_URLS` | Optional JSON device labels → base URLs; Browser is discovered from Seerr |
| `{LIDARR,READARR}_URL`, `*_API_KEY` | Enable a direct music/book integration with both values |
| `{RADARR,SONARR}_URL`, `*_API_KEY` | Legacy overrides only when `SEERR_DISCOVERY=false` |
| `{LIDARR,READARR}_ROOT_FOLDER`, `*_QUALITY_PROFILE_ID`, `*_METADATA_PROFILE_ID` | Defaults for direct album/book additions |
| `ARR_REQUEST_LIMIT_PER_DAY` | `10` album/book requests per user per rolling 24 hours |
| `WEBHOOK_SECRET`, `WEBHOOK_RATE_LIMIT_COUNT` | Optional 32+ character secret; `60` notifications/minute per source/client |
| `WEBHOOK_PUBLIC_URL` | Optional receiver hostname for deployment configuration |

Service URLs omit `/api/v1` and `/api/v3`. Seerr routes movies/TV and file deletion through its own configured Radarr/Sonarr instances; the bot imports every instance before each refresh. Lidarr/Readarr remain direct. Seerr CSRF protection is supported with its XSRF cookie/header; use an **HTTPS `SEERR_URL`** when secure cookies are enabled.

```dotenv
JELLYFIN_DEVICE_URLS={"Browser":"https://jellyfin.example.com","Home":"http://192.168.1.10:8096"}
```

Choose a device in Activity **Preferences** or `/notifications`. Item links use Seerr’s Jellyfin IDs, without tokens in URLs. All URLs must point to the same Jellyfin server. This opens its web client, **not remote playback**. Music/books and items without a Seerr-synced Jellyfin ID do not receive fabricated links.

## Commands

`/activity` · `/dashboard` · `/link` · `/requests` · `/search` · `/library` · `/storage` · `/watchlist` · `/notifications` · `/deletions` · `/status` · `/seerr`

Operators: `/announce` · `/servers` · `/inbox` · `/deliveries`

All command responses are private. `/requests all_requests:true` requires Seerr `REQUEST_VIEW`, `MANAGE_REQUESTS` or `ADMIN`. Movies/TV run as the linked user via `X-API-User`, preserving Seerr quotas and approval policy. TV requests include all seasons; use Seerr for individual seasons/4K. Album/book requests use configured service profiles and a separate daily limit; they are not Seerr requests.

## Admin messages & inbox

Verified linked Seerr owners/Administrators receive **global** bot messaging/inbox access by default, rechecked against live Seerr on each admin operation and queued delivery. `ADMIN_DISCORD_IDS` adds explicit **bot-only exceptions**, without modifying Seerr roles; these need no linked account. Discord server roles alone do not grant bot-admin access. Disable `SEERR_ADMINS` and clear exceptions to disable Discord/Activity operator access and collection; authenticated local-panel operators can still manage the service. With `ALLOWED_GUILD_IDS`, launch commands/Activity from an allowed server; operators may explicitly target any joined server.

```dotenv
ADMIN_DISCORD_IDS=123456789012345678,234567890123456789
ADMIN_ANNOUNCEMENT_CHANNELS={"345678901234567890":"456789012345678901"}
```

`/announce message:...` defaults to the invoking server/channel. Optional `server_id` targets another server's configured announcement channel (or system channel); `recipient` targets one user by DM. `all_servers:true` explicitly selects every joined server, and `all_users:true` additionally DMs their non-bot members, deduplicated across servers. No target in DMs means **no send**, not a global broadcast. Every send requires a private, two-minute recipient preview/confirmation.

Activity and local panel → **Messages** offer server checkboxes, a user-ID target, broadcast preview, delivery counts, and an inbox grouped into **user DMs / servers**. Filter by server, channel, sender, text and newest/oldest; browse 50 messages per page. **Reply** selects the original DM sender or server channel in the composer, without sending immediately. Activity **Open** jumps to server messages in Discord, subject to the operator's own channel permissions. All operators can see the shared inbox and job counts. Local-panel sends are audited as `local-panel`.

- Captures new DMs, direct bot mentions, resolved replies to the bot, and explicitly configured `INBOX_CHANNEL_IDS` while running. It does **not** backfill Discord history, read unrelated channels by default, or record its own outgoing messages. Only attachment counts are stored; attachments are not downloaded.
- DMs/direct mentions do not require Message Content intent. Full non-mention reply/selected-channel content requires `INBOX_MESSAGE_CONTENT=true` **and** Message Content Intent in Developer Portal. Without it, Discord may provide no text. Inferred admin access needs reachable Seerr; explicit exceptions/local-panel inbox access work during media-service outages.
- Retention defaults to **7 days / 10,000 messages**, whichever limit is reached first (`INBOX_RETENTION_DAYS`, `INBOX_MAX_MESSAGES`). Stored content is private operator data; protect database/backups and tell users who can read bot DMs. Pruning runs on receipt/inbox access.
- All-member DMs require `ENABLE_MEMBERS_INTENT=true` **and** Server Members Intent in Developer Portal. Discord can deny DMs; use broad sends only for expected, relevant announcements, never unsolicited promotion.
- Sends are paced (`ADMIN_SEND_INTERVAL_SECONDS=1`), limited to `ADMIN_MAX_RECIPIENTS=1000`, and rate-limited to five previews per five minutes per operator. Mentions cannot ping users/roles/everyone. Audit records persist sender, content, destination and confirmed/failed/uncertain delivery. Unconfirmed sends are **never automatically replayed**; pending sends resume after restart.

## Deletion safety

Only the original requester can schedule removal of their available files. The deadline persists across restarts and is **24 hours after confirmation**. Undo in Activity **Deletions** or `/deletions` while pending, including during upstream outages. No administrator review is required.

After the deadline, the bot refreshes everything and checks the account link, live owner, exact media ID, 4K variant and availability before calling **only** `/media/{id}/file`. Seerr request/media records remain intact. Seerr can remove the whole movie/series from Radarr/Sonarr while deleting files, including shared files; confirmation warns about this.

Outages postpone execution. Changed ownership/target or a revoked link cancels it. Undo cannot stop a deletion already in flight. Ambiguous timeouts/interrupted writes become `uncertain` and are **not automatically retried**; verify the upstream outcome before correcting the local action record.

## Notifications

DMs are **off by default**. Enable them in Preferences or `/notifications`; mute works during outages. Movie/series watchlists sync with Seerr automatically, independently of DM opt-in. Notifications describe verified request/file-availability changes, not raw webhook text or every download-progress event.

### Automatic two-way watchlists

- **Movies/series:** Follow/Unfollow in the hub writes membership to the linked Seerr user's native watchlist. Seerr additions/removals flow back to the hub. Opening `/watchlist` or the Activity watchlist syncs first; a separate background worker also syncs every `SYNC_INTERVAL_SECONDS` (default 300), even when unrelated library/storage refreshes fail. Preferences provides **Sync now**; the old import API remains a compatibility alias for full two-way sync.
- **First sync:** safely merge both lists, preserving existing entries. After that, a persisted per-user baseline tracks removals so deleted entries do not get resurrected. A disappearance on either side removes the item; an explicit pending hub Follow/Unfollow wins a simultaneous conflict. The most recent hub choice supersedes earlier pending choices.
- **Outages/restarts:** save outbound membership changes before sending them. Retries first read complete live membership and acknowledge only confirmed results; an ambiguous response never triggers blind replay. Incomplete, inconsistent, unsupported or inaccessible watchlist responses never count as an empty list. Pending changes and baselines persist in SQLite. Failed watchlist reads do not return a cached fallback.
- **Boundaries:** Jellyfin verification and the live Seerr/Discord identity binding gate synchronization, including background writes. Requests use `X-API-User`, never the administrator's watchlist. A persisted destination/account pin blocks silently sending old intents to a different Seerr URL or account; operators must resolve a deliberate migration. This targets Seerr's native Jellyfin/non-Plex watchlist API, not a Plex-hosted watchlist.
- **Limits:** the combined hub watchlist stays capped at 200 entries. An oversized merge stops without truncating/replacing either list; remove entries before trying again. Music/books stay hub-only. Watching never calls a media-request, file-deletion or Seerr request-deletion endpoint, and never enables DMs automatically.

For each configured service, use **Settings → Connect → Webhook**:

- URL: `https://your-backend/webhooks/radarr` (or `sonarr`, `lidarr`, `readarr`)
- POST; username `bot`; password `WEBHOOK_SECRET`
- Enable relevant import/upgrade/rename/deletion events and **Test**.

For Seerr, use **Settings → Notifications → Webhook**, URL `/webhooks/seerr`, Authorization `Bearer <WEBHOOK_SECRET>`, and payload `{"notification_type":"{{notification_type}}"}`. Custom clients can also use Bearer auth. Webhooks only trigger a verified refresh. Polling catches missed events. Seerr’s native Discord notifications are separate from the bot’s opt-in DMs.

## Persistence & limits

- Atomic snapshots reject failed/incomplete pages; valid empty results clear the cache. Request and media status are separate. Cached title/poster metadata is descriptive; live state controls decisions.
- Freshness means the **latest API-reported state**, not an instantaneous filesystem scan. Keep native Jellyfin/Seerr/*arr scan jobs enabled. An enabled service outage blocks upstream-data operations; local Undo, mute, unfollow and history remain usable.
- SQLite persists mappings, follows, deadlines, state observations and the notification outbox. Back it up securely. Old `linked_users.json` mappings are not silently trusted; users must relink. The old file is not deleted.
- Notifications retry with deduplication/backoff. Closed DMs/revoked links mute delivery. A crash after Discord accepts a DM but before delivery is recorded can produce a duplicate.
- Readarr support requires a compatible installation and working upstream metadata services.

## Tests

```sh
python -m unittest discover -s tests -v
# In activity/:
npm test
npm run build
```

Tests use mocks/local HTTP servers, never production media. OAuth/SDK launch, Quick Connect and your service versions still need live Discord deployment testing before real file deletion.

[MIT License](LICENSE)
