# End-to-end setup

Start with **slash-only mode**. Add the Activity after commands work. No public IP or incoming port is needed for slash-only operation; the bot still needs outbound access to Discord and your media services.

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
3. Under **Bot**, create/configure its bot user. **Reset Token** if necessary and copy its token into `DISCORD_TOKEN`. Never use the OAuth client secret as a bot token or publish either secret.
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

For additional command restrictions, use the server's **Settings → Integrations → application → Commands**. This supplements, but does not replace, the bot's operator ID checks.

## 4. Privileged intents

Go to **Bot → Privileged Gateway Intents**:

| Intent | Developer Portal | Matching `.env` |
| --- | --- | --- |
| Presence | Off | Not used |
| Server Members | On only for all-member announcement DMs | `ENABLE_MEMBERS_INTENT=true` |
| Message Content | On for full non-mention reply text or selected inbox-channel content | `INBOX_MESSAGE_CONTENT=true` |

Keep both environment flags `false` if you do not need those features. DMs and direct bot mentions can be collected without Message Content Intent. Enable an intent **both in the portal and `.env`**, then restart; otherwise Discord may reject the connection or omit data. Larger/verified applications may need Discord's approval for privileged intents.

## 5. Collect IDs and configure `.env`

In your Discord client, enable **User Settings → Advanced → Developer Mode**. Right-click a server, channel or user → **Copy ID**. Use numeric IDs, not names, invite URLs or tokens.

Copy [`.env.example`](../.env.example) to `.env`; each option has inline documentation. At minimum set:

```dotenv
SEERR_URL=http://your-seerr-host:5055
SEERR_ADMIN_KEY=your-seerr-api-key
DISCORD_TOKEN=your-bot-token
ALLOWED_GUILD_IDS=your-server-id
ACTIVITY_ENABLED=false
WEBHOOK_SECRET=
```

Replace placeholders with real values. `ALLOWED_GUILD_IDS` can list multiple comma-separated servers; empty allows all guilds and DM commands. A nonempty allowlist registers guild-scoped commands for those servers. After changing it, restart; Discord may retain previously registered commands in old servers, but runtime access checks still deny them.

Optional operator configuration:

```dotenv
ADMIN_DISCORD_IDS=123456789012345678
ADMIN_ANNOUNCEMENT_CHANNELS={"234567890123456789":"345678901234567890"}
```

Only explicitly trusted user IDs receive **global** admin messaging/inbox access. Discord Administrator roles and Seerr admins do not grant this. Mapping values are existing channel IDs in those guilds. Without a mapping, cross-server/Activity announcements use the guild's system channel; `/announce` in a server defaults to its current channel. Tell users that operators can read DMs sent to the bot; secure database backups.

Set `JELLYFIN_DEVICE_URLS` to your real server URL(s), or `{}` to disable links. Add optional service URL/key pairs together. Keep root/profile IDs unset until you have verified their values in Lidarr/Readarr. All intervals use seconds except `INBOX_RETENTION_DAYS`.

## 6. Run the bot

Install Python 3.10+ and dependencies. From the repository root:

```sh
python -m pip install -r requirements.txt
python bot.py
```

On Windows, `py` can replace `python`, and `Copy-Item .env.example .env` copies the example. On Linux/macOS use `cp .env.example .env`; a Python virtual environment is recommended.

For Docker, follow the [README's Docker section](../README.md#docker). Mount `/data` persistently, ensure UID `10001` can write it, and run one instance per database. The Docker build includes the Activity frontend, even if the runtime toggle is off. Native slash-only installation does not need Node.

Wait for the connection log and command sync. Guild-scoped commands usually appear quickly; global registration can take time. If commands are missing, check the install scopes, allowlist, channel permission and correct bot/application token. Reload Discord if needed.

## 7. First-run checks (slash-only)

1. Run `/link` in an allowed server. Approve its private code in an already signed-in Jellyfin client's **Settings → Quick Connect**. Do not share the code or approve unexpected codes.
2. Run `/status` and confirm a successful refresh; then `/dashboard`, `/library`, `/storage` and `/requests`.
3. Search a movie/series with `/search`, select a result and request it through confirmation. Optional music/books use the same command. Check the resulting upstream request/addition before retrying a failed or ambiguous operation.
4. In `/notifications`, choose a Jellyfin device URL and enable DMs if wanted. Follow an item from its details, check `/watchlist`, and try Unfollow. Import Seerr follows from Notifications if desired.
5. As an operator, run `/servers`, send a test DM/mention to the bot, then `/inbox`. Select a message, open Reply, and **cancel the preview** to confirm nothing sends without approval.
6. Test `/announce` with one private test channel/user, then `/deliveries` and `/deliveries job:ID`. Test multi-server broadcasts only after checking destination mappings and privileged intents.
7. For deletion testing, use **disposable test media**: `/requests` → owned available request → Delete → confirmation; verify it appears in `/deletions`, then select it and Undo. Real deletion runs only after 24 hours and live revalidation. Undo cannot stop an already-started deletion.

Slash commands and their private buttons/modals cover the hub without any hosted interface. Automated tests do not replace these checks against your actual service versions and Discord application.

## 8. Optional embedded Activity

Skip this section for slash-only use.

1. Install Node 22+; run `npm ci` and `npm run build` inside `activity/` (Docker does this automatically).
2. Set `ACTIVITY_ENABLED=true`, `DISCORD_APPLICATION_ID` to this application's ID and `DISCORD_CLIENT_SECRET` to its **OAuth2** client secret.
3. Serve the backend at a **public HTTPS hostname** through a reverse proxy or a tunnel such as Cloudflare Tunnel. Route it to `http://127.0.0.1:8080`, or your configured private listener. `BOT_HOST=127.0.0.1` works for a tunnel/proxy on the same machine; use `0.0.0.0` inside Docker with appropriate private networking. Never expose API keys or logs containing OAuth request bodies.
4. Under **Developer Portal → Activities**, enable Activities and add **URL Mapping prefix `/`** pointing to the HTTPS backend hostname (use the hostname/target format requested by the portal).
5. Under **OAuth2 → Redirects**, add **`https://127.0.0.1`**, the embedded SDK's placeholder redirect. You do not need a separate browser registration page or public redirect handler.
6. Restart the bot. Enable client Developer Mode and use `/activity`. Test as an application owner/team member first; broader Activity distribution may require Discord's testing/distribution/verification settings.
7. Approve the SDK's requested OAuth scopes: `identify`, plus `guilds` when `ALLOWED_GUILD_IDS` is set. These are viewer-authentication scopes, **not** extra bot-install scopes. The backend verifies the viewer, not client-supplied identity claims.

Users still need access to the launch channel and its Use Activities permission. Sessions expire; reopen the Activity to sign in again. If hosting is unavailable, use slash commands; set `ACTIVITY_ENABLED=false` and restart to disable Activity mode completely.

For a temporary local test, install `cloudflared` and run `cloudflared tunnel --url http://127.0.0.1:8080` in a second terminal. Use its generated `*.trycloudflare.com` hostname as the mapping target (no `https://` in the hostname field). Quick-tunnel hostnames change on restart; use a stable named tunnel/hostname for ongoing deployment. See [Discord's official Activity setup guide](https://docs.discord.com/developers/activities/building-an-activity) for portal screenshots. Keep the tunnel running while testing.

## 9. Optional service webhooks

Skip for polling-only use. Set a random `WEBHOOK_SECRET` of at least 32 characters, then restart. This starts the HTTP listener even if Activity is disabled. Local services can reach its private network address; remote services need a reachable HTTPS receiver.

Use the [README's notification instructions](../README.md#notifications) to configure Seerr/Servarr Connect webhooks. Send each integration's Test event and confirm it is accepted. Webhook payloads only wake a verified API refresh; they do not supply trusted messages or authorize actions. No Discord **Interactions Endpoint URL** is needed: slash commands arrive through the bot's gateway connection.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| Bot cannot connect / disallowed intents | Correct bot token; both portal and `.env` intent settings |
| Commands absent or denied | Guild install, scopes, command sync, allowed guild ID, channel/Integration permissions |
| Quick Connect fails | Current Seerr endpoints, enabled Jellyfin Quick Connect, Seerr's Jellyfin URL, network reachability |
| Media operations blocked | `/status`; every enabled service must respond; remove unused integration URL/key pairs |
| Announcement lacks a channel | Mapping/system/current channel; View Channel + Send Messages (+ Threads) |
| DMs blocked | Recipient privacy settings/shared server; inspect `/deliveries job:ID` |
| Inbox has no text | Message Content Intent where necessary; only new messages while online are collected |
| Activity blank / cannot authenticate | Frontend build, HTTPS URL Mapping, client ID matches bot, OAuth client secret, guild membership, distribution access |
| Activity unavailable | Use slash commands; disable `ACTIVITY_ENABLED` if hosting is not wanted |

Rotate any exposed token/key. Never automatically resend an `uncertain` announcement/request/deletion; check the upstream result first.
