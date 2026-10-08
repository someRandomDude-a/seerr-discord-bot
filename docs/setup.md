# End-to-end setup

Start with **slash-only mode** using the [integrated onboarding/admin panel](onboarding.md). Add the Activity after commands work. No public IP or public incoming port is needed for slash-only operation; the separate private setup port is printed at startup. The bot needs outbound access to Discord and your media services. This guide retains the detailed installation checklist and advanced environment reference.

## 1. Prepare media services

1. Configure Jellyfin normally and enable **Quick Connect** in its dashboard. Verify a user can sign in and open **Settings → Quick Connect** in a Jellyfin client.
2. Configure Seerr's Jellyfin connection. Use a current Seerr version providing Jellyfin Quick Connect authentication; older Jellyseerr releases may lack the required endpoints.
3. In Seerr, configure its Radarr/Sonarr connections, root folders and profiles. Test these connections and ensure Seerr receives Jellyfin availability updates.
4. Copy Seerr's administrator API key from **Settings → General**. The bot uses it for refresh/deletion; normal requests use the verified user's identity and Seerr's permissions/quotas.
5. Grant intended Seerr users request permissions. Only users with `REQUEST_VIEW`, `MANAGE_REQUESTS` or `ADMIN` can browse all requests; ownership, not admin status, governs file-deletion scheduling.
6. Optional: configure Lidarr/Readarr and copy their API keys from **Settings → General**. Set existing root-folder paths, quality-profile IDs and metadata-profile IDs for album/book additions. Readarr requires compatible v1 APIs and working metadata providers.

Use URLs reachable **from the machine/container running the bot**. `localhost` inside Docker means that container, not your host. Do not expose Seerr or service keys just to support the Activity. A configured service outage blocks media operations, so leave unused URL/key pairs empty.

## 2. Create the Discord application

1. Open [Discord Developer Portal](https://discord.com/developers/applications) → **New Application**.
2. Under **General Information**, copy the **Application ID**. Use this same application for the bot and optional Activity.
3. Under **Bot**, create/configure its bot user. **Reset Token** if necessary and copy its token into the panel's **Bot token** field (`DISCORD_TOKEN` for headless deployments). Never use the OAuth client secret as a bot token or publish either secret.
4. Leave **Requires OAuth2 Code Grant** disabled for the normal bot install. Choose **Public Bot** according to who may invite it; it does not control operator privileges.

## 3. Installation scopes and permissions

Under **Installation → Installation Contexts**, enable **Guild Install**. Guild installation is required for this bot's server messaging/inbox features. User Install is optional and is not a substitute for joining the server.

Under **Default Install Settings → Guild Install** select:

- Scopes: **`bot`**, **`applications.commands`**.
- Bot permissions: **View Channels**, **Send Messages**, **Embed Links**, **Read Message History**.
- Add **Send Messages in Threads** if announcements/replies will target threads.
- Do **not** grant Administrator, Manage Roles or Manage Messages; these are unnecessary.

If User Install is enabled, its default scope is **`applications.commands`** only. The implementation is intended primarily for guild-installed use; DM command access is blocked when a guild allowlist is configured.

Use the **Discord Provided Link** from Installation to invite the bot to your server. Alternatively, generate an **OAuth2 → URL Generator** link with the same scopes/permissions. Invite as someone who may manage that server. Repeat for every server you want the bot to join.

Check channel permission overrides: the bot must be able to view/send in announcement and reply channels. Human users need **Use Application Commands**. For the optional dashboard, allow **Use Activities / Use Embedded Activities** for the **users** launching it; no additional bot gateway intent is required for Activities.

For additional command restrictions, use the server's **Settings → Integrations → application → Commands**. This supplements, but does not replace, the bot's live Seerr-admin/bot-only exception checks.

## 4. Privileged intents

Go to **Bot → Privileged Gateway Intents**:

| Intent | Developer Portal | Matching panel/advanced setting |
| --- | --- | --- |
| Presence | Off | Not used |
| Server Members | On only for all-member announcement DMs | `ENABLE_MEMBERS_INTENT=true` |
| Message Content | On for full non-mention reply text or selected inbox-channel content | `INBOX_MESSAGE_CONTENT=true` |

Keep both flags `false` if you do not need those features. DMs and direct bot mentions can be collected without Message Content Intent. Enable an intent **both in the portal and panel**, then Save & apply; otherwise Discord may reject the connection or omit data. Larger/verified applications may need Discord's approval for privileged intents.

## 5. Collect IDs and configure the service

In your Discord client, enable **User Settings → Advanced → Developer Mode**. Right-click a server, channel or user → **Copy ID**. Use numeric IDs, not names, invite URLs or tokens.

Normally start `python bot.py`, open the printed private setup URL, and complete [the wizard](onboarding.md#wizard-verified-steps). It verifies Discord/Seerr, imports Seerr's Radarr/Sonarr settings and saves credentials in `DATA_DIR/settings.json`. [`.env.example`](../.env.example) now contains only optional bootstrap settings.

For a legacy/headless install, [`.env.advanced.example`](../.env.advanced.example) documents every setting. Environment entries override saved settings. At minimum configure:

```dotenv
SEERR_URL=http://your-seerr-host:5055
SEERR_ADMIN_KEY=your-seerr-api-key
DISCORD_TOKEN=your-bot-token
ALLOWED_GUILD_IDS=your-server-id
ACTIVITY_ENABLED=false
WEBHOOK_SECRET=
PANEL_ENABLED=false
```

Replace placeholders with real values. `ALLOWED_GUILD_IDS` can list multiple comma-separated servers; empty allows all guilds and DM commands. A nonempty allowlist registers guild-scoped commands for those servers. Save & apply (or restart headless installs) after changing it; Discord may retain previously registered commands in old servers, but runtime access checks still deny them.

Optional operator configuration:

```dotenv
ADMIN_DISCORD_IDS=123456789012345678
ADMIN_ANNOUNCEMENT_CHANNELS={"234567890123456789":"345678901234567890"}
```

Verified linked Seerr owners/Administrators receive **global** bot messaging/inbox access by default. `ADMIN_DISCORD_IDS` adds trusted **bot-only exceptions** without changing Seerr roles; Discord Administrator roles alone do not grant access. Inferred authority is rechecked against live Seerr for every admin operation/delivery. Mapping values are existing channel IDs in those guilds. Without a mapping, cross-server/Activity/panel announcements use the guild's system channel; `/announce` in a server defaults to its current channel. Tell users that operators can read bot DMs; secure database backups.

Set `JELLYFIN_DEVICE_URLS` to your real server URL(s), or `{}` to disable links. Radarr/Sonarr are imported from Seerr, including all 4K/default/nondefault instances; unused legacy variables are ignored unless `SEERR_DISCOVERY=false`. Lidarr/Readarr remain direct URL/key pairs. Keep root/profile IDs unset until verified in those services (the panel provides dropdowns). All intervals use seconds except `INBOX_RETENTION_DAYS`.

## 6. Run the bot

Install Python 3.10+ and dependencies. From the repository root:

```sh
python -m pip install -r requirements.txt
python bot.py
```

On Windows, `py` can replace `python`; a Python virtual environment is recommended. No `.env` is required for panel onboarding. Existing environment configurations still work; their fields are read-only in the panel until the overrides are removed.

For Docker, follow the [README's Docker section](../README.md#docker). Mount `/data` persistently, ensure UID `10001` can write it, and run one instance per database. The Docker build includes the Activity frontend, even if the runtime toggle is off. Native slash-only installation does not need Node.

If configuration is incomplete, the bot waits while the separate panel is available. Complete verification and Save & apply, then wait for the connection log/command sync. Guild-scoped commands usually appear quickly; global registration can take time. If commands are missing, check install scopes, allowlist, channel permissions and the correct bot/application token. Reload Discord if needed.

## 7. First-run checks (slash-only)

1. Run `/link` in an allowed server. Approve its private code in an already signed-in Jellyfin client's **Settings → Quick Connect**. Do not share the code or approve unexpected codes.
2. Run `/status` and confirm a successful refresh; then `/dashboard`, `/library`, `/storage` and `/requests`.
3. Try `/discover` for popular movie/series poster cards, or `/search` for a title. Select a card, inspect its synopsis and request through confirmation. Back should preserve the gallery position; Next should cross search batches without typing a new command. Test Filter titles and category selection too. Optional music/books use `/search`. Check the resulting upstream request/addition before retrying a failed or ambiguous operation.
4. Open an available movie/show in Jellyfin. Browser is discovered from Seerr's Jellyfin external hostname (or its internal address if unset); no device URL entry is required. The bot resolves items using the verified user's Jellyfin permissions, even when Seerr lacks an item ID. In `/notifications`, optionally choose an extra destination and enable DMs. Follow a movie/series, check that it appears in your Seerr watchlist, then Unfollow and check removal. Add/remove an entry in Seerr and verify the hub converges on its next watchlist open or background sync (default 5 minutes). Preferences has **Sync watchlists now**; no manual import or DM opt-in is required. Music/books remain local.
5. As an operator, run `/servers`, send a test DM/mention to the bot, then `/inbox`. Select a message, open Reply, and **cancel the preview** to confirm nothing sends without approval.
6. Test `/announce` with one private test channel/user, then `/deliveries` and `/deliveries job:ID`. Test multi-server broadcasts only after checking destination mappings and privileged intents.
7. For deletion testing, use **disposable test media**: `/requests` → owned available request → Delete → confirmation; verify it appears in `/deletions`, then select it and Undo. Real deletion runs only after 24 hours and live revalidation. Undo cannot stop an already-started deletion.

Slash commands and their private buttons/modals cover the hub without any hosted interface. Automated tests do not replace these checks against your actual service versions and Discord application.

Discovery/details/links read their own upstream dependencies live rather than refreshing unrelated storage/library integrations. Opening or refreshing snapshot-backed collections still performs a fresh sync. Browsing controls reuse a clearly labelled, already-open gallery snapshot; native controls recheck the viewer's identity/permissions before redisplaying it, and all mutation endpoints perform their own live checks. A failed collection refresh never falls back to the old gallery.

In the Activity, Discover shows popular titles immediately. Test poster details, Back, collection pagination, saved filters and the verified **Open Seerr** shortcut. Seerr opens externally and requires the user's own browser login/session: embedding it with bot/admin credentials is not supported. Its cookie/CSRF rules cannot safely be bypassed just to embed it under Discord's origin.

Watchlist sync requires Seerr's native `/discover/watchlist`, `POST /watchlist` and `DELETE /watchlist/{tmdbId}?mediaType=movie|tv` endpoints. They always run as the verified linked Seerr user. On the first sync both lists are merged; subsequent removals propagate in either direction. Pending hub choices win conflicts, survive restarts, and are retried only after reading fresh membership. Test an upstream outage and a restart while a removal is pending: the entry must not reappear locally and should disappear remotely after recovery. Revoked verification pauses sync without wiping lists. Unsupported/malformed responses stop sync instead of acting like an empty watchlist. Keep the full SQLite database and `jellyfin.key` mounted; do not delete sync tables or the database to resolve a destination change. See [watchlist sync rules](../README.md#automatic-two-way-watchlists) for migration and size-limit behavior.

### Identity and privacy checks

Before `/link`, verify that `/status`, `/notifications`, library/storage commands and Activity posters disclose no server details; even bot-only operators must prove their Jellyfin identity. Existing links from older images need one new approval. After linking, restart the bot and confirm the identity persists. Revoke that Discord Media session in Jellyfin and confirm private reads/images now require `/link` again. A Jellyfin/Seerr outage must block disclosures without deleting a retained token.

Persist/back up the SQLite database and its sibling `jellyfin.key` together, and protect both from other users. Tokens are encrypted in SQLite, but someone who obtains the database **and** key can decrypt them. The key is created with Unix mode `0600`; protect Windows ACLs and backups explicitly. Seerr remains the authority for request permissions/quotas, while Jellyfin's live user token determines identity. Browser links never contain that token.

## 8. Optional embedded Activity

Skip this section for slash-only use.

1. Install Node 22+; run `npm ci` and `npm run build` inside `activity/` (Docker does this automatically).
2. Choose the panel's Activity preset; set `ACTIVITY_ENABLED=true`, `DISCORD_APPLICATION_ID` to this application's ID and `DISCORD_CLIENT_SECRET` to its **OAuth2** client secret.
3. Serve the bot backend at a **public HTTPS hostname** through a reverse proxy or tunnel such as Cloudflare Tunnel. Route it to `http://127.0.0.1:8080`, or your configured private backend listener—**never the setup/admin port**. `BOT_HOST=127.0.0.1` works for a tunnel/proxy on the same machine; use `0.0.0.0` inside Docker with private networking. Never expose API keys or logs containing OAuth bodies.
4. Under **Developer Portal → Activities**, enable Activities and add **URL Mapping prefix `/`** pointing to the HTTPS backend hostname (use the hostname/target format requested by the portal).
5. Under **OAuth2 → Redirects**, add **`https://127.0.0.1`**, the embedded SDK's placeholder redirect. You do not need a separate browser registration page or public redirect handler.
6. Save & apply (or restart headless installs). Enter the backend hostname as **Public backend URL** and test it in the panel. Enable client Developer Mode and use `/activity` to verify SDK/OAuth; test as an application owner/team member first. Broader distribution may require Discord's testing/distribution/verification settings.
7. Approve the SDK's requested OAuth scopes: `identify` and `guilds`. These are viewer-authentication scopes, **not** extra bot-install scopes. The backend verifies the viewer, not client-supplied identity claims. Discord OAuth alone reveals no private server data: a valid retained Jellyfin token is required too.

Users still need access to the launch channel and its Use Activities permission. Sessions expire; reopen the Activity to sign in again. If hosting is unavailable, use slash commands; set `ACTIVITY_ENABLED=false` and restart to disable Activity mode completely.

For a temporary local test, install `cloudflared` and run `cloudflared tunnel --url http://127.0.0.1:8080` in a second terminal. Use its generated `*.trycloudflare.com` hostname as the mapping target (no `https://` in the hostname field). Quick-tunnel hostnames change on restart; use a stable named tunnel/hostname for ongoing deployment. See [Discord's official Activity setup guide](https://docs.discord.com/developers/activities/building-an-activity) for portal screenshots. Keep the tunnel running while testing.

## 9. Optional service webhooks

Skip for polling-only use. Generate the webhook credential in the panel (or set a random `WEBHOOK_SECRET` of at least 32 characters), then Save & apply. This starts the **bot backend** listener even if Activity is disabled; it remains separate from the local admin port. Local services can reach its private network address; remote services need a reachable HTTPS receiver.

Use the [README's notification instructions](../README.md#notifications) to configure Seerr/Servarr Connect webhooks. Send each integration's Test event and confirm it is accepted. Webhook payloads only wake a verified API refresh; they do not supply trusted messages or authorize actions. No Discord **Interactions Endpoint URL** is needed: slash commands arrive through the bot's gateway connection.

## Troubleshooting

### SQLite storage permissions

`sqlite3.OperationalError: attempt to write a readonly database` means SQLite cannot write to its file, directory or WAL/shared-memory sidecars. The schema migration is legitimate; do not delete the database to bypass it. PyNaCl/davey warnings only concern voice, which this bot does not use.

For Docker, explicitly configure:

```dotenv
DATA_DIR=/data
DATABASE_PATH=seerr_cache.db
```

Mount the **whole directory** read-write, for example `./data:/data` (not `:ro`), or a named volume. `.env` values override image defaults; `DATA_DIR=./data` means `/app/data` inside this image, not `/data`. SQLite needs to create/update `seerr_cache.db-wal` and `seerr_cache.db-shm` in the same directory.

For an existing bind mount or volume with mismatched ownership, stop the bot and back up its data directory including any sidecars. On a Linux Docker host, use a one-off root container to repair only the mounted directory and the three database files. Replace `discord-bot` with your **Compose service name** (not the `...-1` container name), and adapt paths if you configured a different database:

```sh
docker compose stop discord-bot
docker compose run --rm --no-deps --user 0 --entrypoint sh discord-bot -c 'chown 10001 /data && chmod u+rwx /data && for file in /data/seerr_cache.db /data/seerr_cache.db-wal /data/seerr_cache.db-shm; do if [ -e "$file" ]; then chown 10001 "$file" && chmod u+rw "$file" || exit 1; fi; done'
docker compose up -d --build discord-bot
```

This does not delete files or run the bot as root permanently. If ownership changes are disallowed by your filesystem (Windows/network shares/rootless Docker), correct host ACLs or migrate a stopped, backed-up database into a writable named volume. SELinux hosts may need an appropriate bind-mount label (`:Z` for a private mount). Do not use `chmod 777`, remove the volume, or discard the SQLite sidecars. A read-only mount must be corrected in Compose before permission repair can work.

| Symptom | Check |
| --- | --- |
| Bot cannot connect / disallowed intents | Correct bot token; both portal and panel intent settings |
| Commands absent or denied | Guild install, scopes, command sync, allowed guild ID, channel/Integration permissions |
| Quick Connect fails | Current Seerr endpoints, enabled Jellyfin Quick Connect, Seerr's Jellyfin URL, network reachability |
| Media operations blocked | `/status`; every enabled service must respond; remove unused integration URL/key pairs |
| Announcement lacks a channel | Mapping/system/current channel; View Channel + Send Messages (+ Threads) |
| DMs blocked | Recipient privacy settings/shared server; inspect `/deliveries job:ID` |
| Inbox has no text | Message Content Intent where necessary; only new messages while online are collected |
| Activity blank / cannot authenticate | Frontend build, HTTPS URL Mapping, client ID matches bot, OAuth client secret, guild membership, distribution access |
| Activity unavailable | Use slash commands; disable `ACTIVITY_ENABLED` if hosting is not wanted |

Rotate any exposed token/key. Never automatically resend an `uncertain` announcement/request/deletion; check the upstream result first.
