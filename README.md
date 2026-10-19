# Watchlist Deletion and Discord Integration for Jellyfin 12

This repository contains **two separate in-process .NET 10 plugins** for Jellyfin 12:

| Plugin | Install it for | Depends on the other project plugin? |
| --- | --- | --- |
| **Watchlist Deletion (Preview)** | Native watchlists, notifications, deletion requests/consent and administrator monitoring | **No.** Works without Discord. |
| **Media Hub Discord Integration (Preview)** | Discord commands, private notifications, messaging, Activity and Private SyncPlay controls | **Yes.** Requires Watchlist Deletion. |

Install just the first plugin for native deletion requests, or install **both** for Discord features.
The core keeps the historical `Jellyfin.Plugin.MediaHub` assembly name to preserve existing identity/state;
this does **not** mean the Discord plugin is bundled into it.
No separate application server, admin listener or runtime service is installed.

> **Preview, not production-ready.** Use a test Jellyfin; use a separate test Discord application if installing the adapter.
> Automatic deletion defaults off. The Discord plugin additionally requires the separately installed
> Private SyncPlay provider; see [integration and limits](plugin/WATCHPARTY.md). Full split/provider/client acceptance remains incomplete.
> See the [feature checklist](plugin/FEATURES.md) and [security review](plugin/SECURITY_REVIEW.md).

## Install the two plugins

Both currently target **Jellyfin 12.2.0 / .NET 10**. Older Jellyfin releases are unsupported.
You do not need Node, a .NET SDK, a sidecar or an extra listener to install the packaged plugins.
Use a **split-build** package: an older all-in-one Media Hub release is not the two-plugin build.

### 1. Install Watchlist Deletion — native deletion requests, no Discord required

1. Install Jellyfin-12.2-compatible **File Transformation**, then **Plugin Pages**, from their
   [upstream catalog](https://www.iamparadox.dev/jellyfin/plugins/manifest.json).
   These separate third-party dependencies provide the native Web pages/controls;
   see [compatible versions](plugin/WEB_DEPENDENCIES.md).
2. Add [this project's catalog](#catalog-installation-versus-manual-zip-installation), then install
   **Watchlist Deletion (Preview)** from **Dashboard → Plugins → Catalog**.
   Restart Jellyfin after installing the plugins. Manual ZIP installation is an optional fallback.
3. Open **Dashboard → Plugins → My Plugins → Watchlist Deletion (Preview) → Settings**.
   Configure Jellyfin/Seerr connections, approved Arr destinations and deletion permissions here.
   Keep automatic removal off while validating setup. Users sign in with their normal Jellyfin accounts.

**Stop here if you only want native watchlists and deletion requests.** No Discord application,
bot token, account link or Private SyncPlay installation is needed for the core.

### 2. Install Discord Integration — optional, requires the core

1. Install Watchlist Deletion as above.
2. Separately install **Private SyncPlay 0.1.5**, exposing public API/SDK **1.1.0**, using its
   [upstream installation instructions](https://github.com/someRandomDude-a/private-syncplay-groups).
   The SDK DLL bundled in the Discord ZIP is **not** the provider plugin.
3. From the **same catalog**, install the separate **Media Hub Discord Integration (Preview)**
   entry. Restart Jellyfin. It is not bundled into the core or installed automatically with it.
4. Open this plugin's **own** settings page under **Dashboard → Plugins → My Plugins →
   Media Hub Discord Integration (Preview) → Settings**. Configure the bot token and explicit
   server allowlist; configure the matching application ID/OAuth secret for OAuth linking or
   Activity. Enable the bot only when ready. Use one server instance per bot token.
5. Users run `/hub-link` in Discord and approve their own account link in their signed-in
   Jellyfin browser. Follow [Activity setup](plugin/ACTIVITY.md) if enabling Activity.

Core service/deletion settings stay in **Watchlist Deletion**; Discord settings belong to
**Discord Integration**. Do not copy a development `.env` into Jellyfin: plugin configuration
is entered through these administrator pages, and secrets are not returned on readback.

### Catalog installation versus manual ZIP installation

Both plugins use the same catalog URL:

```text
https://somerandomdude-a.github.io/seerr-discord-bot/manifest.json
```

Add it once under **Dashboard → Plugins → Repositories**, not as a Git clone URL, then
install each desired plugin separately from **Catalog**. The **Publish Jellyfin plugin repository**
workflow builds both separate ZIPs, preserves both release histories and publishes both entries
only after its checks pass. Dependencies still need separate installation; Jellyfin does not
automatically install the other project plugin or Private SyncPlay.

**Source changes are not a live release.** Until this updated publisher has been run successfully,
the public catalog may still contain only older entries. If either split entry is missing, wait
for its release or use a matching manual preview package; do not substitute an older monolith.

For a ZIP install:

1. Obtain the core from a matching split-build asset on
   [GitHub Releases](https://github.com/someRandomDude-a/seerr-discord-bot/releases).
   Both ZIPs are published as separate assets/releases by the shared publisher. For an
   unpublished development preview only, obtain the adapter from **GitHub Actions →
   Discord integration CI → successful run → Artifacts → discord-integration-development-only**.
   Verify the selected ZIP against its accompanying `SHA256SUMS`. The core asset is
   `Jellyfin.Plugin.MediaHub_<core-version>.zip`; the separate Discord asset is
   `Jellyfin.Plugin.MediaHub.Discord_<discord-version>.zip`.
   Discord CI's **discord-integration-development-only** download may wrap the actual
   plugin ZIP: unpack that outer artifact first. Do not install the outer artifact or source-code ZIP.
2. Stop Jellyfin. Extract the **complete contents** of each selected plugin ZIP into its
   own versioned folder inside Jellyfin's configured `plugins` directory (commonly
   `/config/plugins` inside the official Docker container):

   ```text
   plugins/
     MediaHub_<core-version>/
       Jellyfin.Plugin.MediaHub.dll
       Jellyfin.Plugin.WatchlistDeletion.Api.dll
       ...remaining files from the core ZIP
     MediaHub.Discord_<discord-version>/
       Jellyfin.Plugin.MediaHub.Discord.dll
       Jellyfin.Plugin.WatchlistDeletion.Api.dll
       Jellyfin.Plugin.PrivateSyncPlay.Api.dll
       ...remaining files from the Discord ZIP
   ```

   Replace the version placeholders with the actual package versions. Keep the two folders
   separate; do not merge DLLs, move bundled SDK copies to a shared directory, or copy host DLLs.
   Omit the Discord folder for a core-only installation. Install third-party providers separately.
   For Docker, `/config/plugins` is a container path: use the corresponding directory in your
   mapped configuration volume. On native Windows/Linux, use Jellyfin's configured plugin path,
   not its application executable directory.
3. Start Jellyfin and verify each installed plugin is **Active** under **My Plugins**.
   Check each plugin's own settings page. The Discord plugin is not standalone and its
   gateway/player controls stay unavailable without both core and provider dependencies.

For the available build workflows, see [building and publishing each plugin](plugin/README.md#build-and-test).
Core and Discord release numbers may differ; choose releases explicitly compatible with the
same Jellyfin ABI and **Watchlist Deletion API 1.0**. Do not assume an arbitrary older release is compatible.

### Upgrading from the older all-in-one Media Hub

Pause automatic removal and the bot explicitly during maintenance. Stop Jellyfin and take
a consistent backup of `<Jellyfin data>/media-hub/` and, if present, `media-hub-discord/`,
including databases/WAL, settings, keys and authentication roots. The new core retains the old
plugin identity and `media-hub/` data; **do not reset it or move its database/key into the Discord folder**.
Use Jellyfin's supported upgrade flow, or preserve the old plugin **binary** folder outside
the scanned `plugins` directory before a manual replacement. Do not load the old monolith
alongside the split core, or leave duplicate copies of the same plugin version.

Install the core and, if wanted, the separate Discord plugin/dependencies, then restart.
Open the core's settings first and explicitly review/save any legacy configuration marked
as requiring review. If installing the adapter, open **Discord Integration → Settings** next,
review the retained legacy configuration and explicitly
confirm its first save/import. Original core settings, approved links and histories remain intact;
new Discord configuration is encrypted under `media-hub-discord/`. Verify retained state before
re-enabling anything. Missing/corrupt state requires matching stopped backups, never deleting keys
or re-importing defaults. See [migration and recovery](plugin/discord/README.md#configuration-ownership-and-safe-migration).

## Features

- Native Jellyfin account/session verification, current library visibility, separate administrator
  integration settings and personal preferences; no administrator impersonation of viewers.
- User-scoped Seerr discovery, movie/TV request previews and confirmation, permissions/quotas,
  request history and two-way watchlist membership. Seerr remains authoritative for those requests.
- Accessible movie/TV/music/book libraries, selected Lidarr/Readarr requests, service/storage
  projections, personal subscriptions, status feed and opt-in Discord notifications.
- Discord media commands and separately authorized operator inbox, messages, attachments and
  previewed announcements. Ambiguous requests/messages are not automatically resent.
- Anonymous shared deletion queue, owner-only Undo, current recent-viewer consent and a full
  delay. Removal is through verified Arr file APIs only; no direct media filesystem deletion.
- Discord-plugin-owned optional web Activity using the core API and the same permission checks.
  It cannot configure the host or impersonate a native player. Launch it through Discord's App Launcher.

This list describes implemented preview functionality, not completed live-provider acceptance.
See [FEATURES.md](plugin/FEATURES.md) for supported paths and remaining work.

## Safety and state

The core owns **`<Jellyfin data>/media-hub/`**; the adapter owns encrypted configuration under
**`<Jellyfin data>/media-hub-discord/`**. Neither directly opens/migrates Jellyfin's
databases, persists web-file edits or changes host/network settings. Uninstall retains plugin
state. Corrupt, foreign, incompatible or unverifiable state is not silently reset or approved.
Back up both plugin directories together while stopped, with their matching encryption keys.

Configuration, user data and external responses are untrusted. Integration settings are
authenticated; discovered Arr destinations additionally require independent administrator
approval. In-process resource/provider/filesystem risks and unresolved release blockers remain;
unit tests cannot establish absolute host immunity or production safety.

## Develop and release

Requires **.NET 10 SDK** for development. **Node 22.12+** is needed for browser tests
and the adapter's Activity build, not for the core assembly alone. Run from the repository root:

```sh
dotnet test plugin/tests/MediaHub.Tests/MediaHub.Tests.csproj -c Release
dotnet test plugin/tests/MediaHub.Tests/MediaHub.Tests.csproj -c Release -p:JellyfinVersion=12.2.0
npm ci --prefix plugin/web
npm test --prefix plugin/web
npm ci --ignore-scripts --prefix plugin/activity
npm run build --prefix plugin/activity
dotnet test plugin/tests/MediaHub.Discord.Tests/MediaHub.Discord.Tests.csproj -c Release -p:RequireActivityBundle=true
```

Code and resources live in `plugin/src/`; native tests in `plugin/tests/`; browser tests in
`plugin/web/`; the bundled Activity source in `plugin/activity/`; packaging and guarded disposable
Jellyfin acceptance tools in `plugin/tools/`. CI tests these plugin components; release tooling
builds two separate allowlisted ZIPs, checksums and a combined `manifest.json`, then publishes immutable preview assets
and the catalog only when explicitly run.

### Documentation

- [Install the two plugins](#install-the-two-plugins) · [Connection settings](docs/onboarding.md) · [Detailed plugin reference](plugin/README.md)
- [Activity configuration](plugin/ACTIVITY.md) · [Implementation plan](plugin/IMPLEMENTATION_PLAN.md)
- [Feature checklist](plugin/FEATURES.md) · [Safety boundaries](plugin/SAFETY.md)
- [Security findings](plugin/SECURITY_REVIEW.md) · [Acceptance evidence](plugin/ACCEPTANCE.md)
- [Packaging and publishing](plugin/README.md#build-and-test) · [Release notes](plugin/repository/RELEASE_NOTES.md)

MIT licensed. See [LICENSE](LICENSE) and [third-party notices](plugin/THIRD_PARTY_NOTICES.md).
