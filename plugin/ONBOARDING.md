# Media Hub settings reference

Use **Dashboard → Plugins → Media Hub → settings** as a Jellyfin administrator, on the latest
stable Jellyfin (**12.2.0**). This is a conventional settings workspace, not a guided sequence.

## Using the settings UI

- Choose **Settings section** to view a connection or permission group. Original controls and
  drafts stay in place; switching sections never saves or changes a service.
- Every field has a **Help** button. Discord installation permissions and Activity connection
  details have expandable help. The callback and directory mapping are calculated from the saved
  public Jellyfin address, not guessed from proxy headers.
- **Save server settings** is the single explicit save boundary for every section. The unsaved
  changes list names all changed fields, including other sections, without showing secret values.
- **Reload saved settings** asks before discarding drafts across sections. Failed/conflicting saves
  keep drafts for correction. Account changes, access loss and page teardown clear credentials and
  private discovery results. No wizard progress, settings drafts or secrets are stored in browser storage.
- An empty password field keeps its saved secret. Enter a replacement, or explicitly choose
  **Remove saved secret when settings are saved**. Removing credentials requires confirmation and
  compatible disabled/disconnected settings; it never deletes accounts, history, watchlists or media.
- A saved revision is not a connection test or production approval. Ordinary users cannot access
  server settings or path/profile discovery. Personal notification preferences remain separate.

## Jellyfin and Seerr

**Public Jellyfin address** is the existing user-facing HTTPS base, including any `/jellyfin`
prefix. Do not add `/web`, a login page, query, fragment or token. Media Hub uses Jellyfin's own
listener; no new application runtime, hosting, port, DNS or VPN changes are required.

**Seerr server address** is reachable from the Jellyfin process/container, without `/api/v1`.
Its **Seerr API key** comes from Seerr → Settings → General. Use Seerr's supported account import
so each viewer has one unambiguous Jellyfin mapping. Seerr owns permissions, quotas, routing and
existing request history; an administrator identity is never substituted for a viewer.

## Discord bot

1. Create a separate test application in the Discord Developer Portal. **Bot → Token** supplies
   the bot token; this is not the Application ID or OAuth client secret. Keep secrets out of URLs,
   chat, screenshots, issues and frontend files. Leave Interactions Endpoint URL blank.
2. Installation → **Guild Install**, scopes **bot** and **applications.commands**. Allow View
   Channels, Send Messages, Embed Links and Read Message History; Attach Files/Send Messages in
   Threads only when used. Do not grant Administrator, management permissions, mass mentions or
   voice permissions. Presence intent stays off. Human users need Use Application Commands.
3. Discord → User Settings → Advanced → Developer Mode; right-click each approved server →
   Copy Server ID. Enter these in **Allowed Discord server IDs**. Blank is not allow-all. User,
   role, application and server IDs are not interchangeable.
4. Save the token, addresses and allowlist, then explicitly enable **Bot connection** when ready.
   Use only one plugin instance per token. Changes reconnect Discord, not Jellyfin.

Users run `/hub-link`, then approve their own code in their own signed-in Jellyfin account.
OAuth linking separately verifies both browser identities and requires explicit confirmation.
Never approve someone else's code while signed in as an administrator.

## Account linking and Discord Activity

Use the same application's **Application ID** and **OAuth2 Client Secret** together. Keep PKCE
enabled when supported. The bot token is a different credential.

Expand **Activity and account-link connection details** for saved-address-specific values:

- OAuth2 redirects: the exact `.../MediaHub/Link/OAuth/Callback` and SDK placeholder
  `https://127.0.0.1`. The placeholder does not require an exposed loopback server.
- Enable supported Activity platforms. URL Mappings: prefix `/`, target the displayed
  `host/base/MediaHub/Activity/` directory **without https://**, not `/App`, `/web` or a file.
- Launch with Discord's **App Launcher / managed Launch entry point**. Media Hub does not
  register `/activity`. Human launchers need Use Activities, allowed-server membership and
  application test/distribution access. Bot installation does not publish an Activity.

Enable **Discord Activity** only after those settings and HTTPS reachability are ready. Do not
weaken origin, authentication, state, cookie, TLS or frame checks to repair a configuration error.
Activity gets no native credential and cannot configure Jellyfin or impersonate a player.
See [ACTIVITY.md](ACTIVITY.md) for detailed transport/acceptance limits.

**Signed notification wake-ups** is optional for a producer supporting the in-place HMAC protocol.
It accelerates verified reads only; ordinary static/unsigned webhooks must use the existing polling
fallback instead. See [NOTIFICATIONS.md](NOTIFICATIONS.md) for limits and the exact signature bytes.

## Music and book requests

Save the **Lidarr/Readarr server address and API key** with **Request access → Browse only**.
Then select **Load saved service folders and profiles**, and choose the existing request
destination folder, quality profile and metadata profile by name. Nothing is selected automatically.
Unsaved addresses/keys are never used for discovery; no folders or service defaults are changed.

Enable selected-album/book-edition requests only after choosing valid destinations. The rolling
daily limit counts uncertain writes. Current Seerr permission is still required. Only the selected
album/edition is added; no parent-wide searches, exclusion removal or unrelated edition changes.
**Disconnect … on next Save** stages removal of this connection only, with explicit credential
removal confirmation. Existing service records, media and request receipts remain intact.

## Messaging access and retention

Messaging operators can read the bot's **shared private inbox**. Enter trusted people's Discord
user IDs, not roles. A Jellyfin administrator or Discord moderator is not automatically an operator.
Only deliberately enabled, verified Seerr administrators may supplement the explicit list.

**Server-message content** requires Message Content Intent both here and in Developer Portal →
Bot → Privileged Gateway Intents. **All-member announcement support** requires Server Members
Intent on both sides. Neither is needed for slash commands or Activity sign-in. Enabling support
does not send a broadcast: announcements still need a recipient preview and explicit confirmation.

Choose retained channel IDs, inbox retention and recipient limit deliberately. Recipient privacy,
bot/channel/member permissions and native links remain live requirements. An uncertain send may
already have happened; inspect the delivery receipt rather than automatically resending.

## Media removal and approved servers

Keep **Approved media removal → Do not automatically remove media** during setup. Defaults are
14 days of recent-viewer consent and a full 2-day delay. New activity invalidates consent;
personal increases extend queues and deadlines never silently shorten. No administrator ownership bypass.

**Find servers configured in saved Seerr** reads Seerr only. It does **not** probe or approve an
Arr address. Independently verify each exact service, Seerr instance ID and base address, add only
the approved destination to the structured list, then Save. No raw JSON or guessed profile IDs are
required. Remove/re-add an approval explicitly when its address changes. Radarr/Sonarr file APIs,
not direct filesystem deletion or broad record/history deletion, perform approved removals.

## Safe first checks

Use an ordinary linked user to browse permitted media, inspect request permissions/quotas, and
open Seerr with that user's own account. Prepare is a preview; Confirm actually sends a request.
**Refresh request receipt** reads an existing plan's outcome without resending it. Only an unsent
plan may be cancelled locally; upstream request management remains in Seerr.

Private watch parties require the separately installed matching provider; see [WATCHPARTY.md](WATCHPARTY.md).
Real native device previews and retained command receipts are described in [PLAYBACK.md](PLAYBACK.md).
Actual provider, Discord/OAuth/Activity, restricted-library, multi-player and custom-skin acceptance
remain separate from saving settings or passing automated tests. See [FEATURES.md](FEATURES.md).
