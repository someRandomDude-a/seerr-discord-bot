# Watchlist Deletion for Jellyfin 12 · development preview

**Two-plugin split in progress:** native watchlists/notifications/deletion queues
keep the existing plugin GUID and retained state. The separate
[Discord Integration](discord/README.md) uses the [public API](PUBLIC_API.md).
The core no longer bundles Discord.Net or the Private SyncPlay SDK, and native
background queue maintenance does not require a Discord link. Separate packaging
and tests exist. Independent encrypted adapter configuration, administrator settings,
Activity routes/assets and OAuth-session ownership are now implemented; dual-plugin
exact-ZIP acceptance is not complete. The r13 evidence below predates this split
and must not be represented as split-build acceptance.

The initial [administrator monitoring workspace](ADMIN_MONITORING.md) now browses
backlog/retained proposal history and private identity/activity/consent evidence.
It explicitly distinguishes current checkpoints from the complete historical
transition journal that older builds never recorded.

The newer [deletion experience increment](DELETION_EXPERIENCE.md) adds passive
personal status/required actions and a focused notification-preferences page with
customizable deletion-notice text. Configurable deletion permissions, verified native
movie/whole-series proposals, a native-menu extension and authenticated transition
history are implemented in source; their full runtime acceptance remains unfinished. Source tests are not
production or exact-ZIP browser acceptance.

Media Hub is an **in-process C#/.NET 10 plugin** for Jellyfin 12. It uses Jellyfin's existing
authenticated pages and listener, with no additional runtime, sidecar or admin port.
It is **not yet feature-complete**. The [implementation plan](IMPLEMENTATION_PLAN.md)
tracks the remaining Discord, Activity, media and safety features.
Native Web controls require separately installed, Jellyfin-12.2-compatible **File Transformation**
and **Plugin Pages**. See [dependency setup and upstream compatibility guidance](WEB_DEPENDENCIES.md).
The latest security, Discord-surface and personal-navigation changes are recorded in
[REMEDIATION.md](REMEDIATION.md), with separate live-service acceptance limits.
The [completion handoff](COMPLETION.md) lists local verification tools and the exact external
access/human acceptance needed for the remaining gates; none is silently waived.

**There are two independently installed plugins.** Install **Watchlist Deletion (Preview)**
for native deletion requests/watchlists, and optionally install **Media Hub Discord Integration
(Preview)** for Discord. See [installation](#install-the-core-and-the-discord-plugin) below.
Open the **core's** settings as a Jellyfin administrator for service connections, approved Arr
destinations and deletion permissions; open the **Discord plugin's separate** settings for
bot/OAuth/Activity/messaging configuration. The core's **Server connections and permissions**
workspace provides section dropdowns, contextual Help and saved-service profile/folder choices.
Each page has its own explicit save boundary; switching sections never saves.
Draft review, explicit credential removal, disconnect controls and confirmed reload replace the
walkthrough. Nothing is auto-enabled and no progress/drafts/secrets enter browser storage.
The [settings reference](ONBOARDING.md) is supplementary to the in-place help.

**Fresh installation:** users create their own links/settings. Existing upstream Seerr requests
are retained, not automatically deleted or recreated.
Evaluate this preview on a test Jellyfin with a separate test Discord application.
Use one plugin instance per Discord application token.

## Jellyfin safety boundary

- Core state is isolated under **`<Jellyfin data>/media-hub/`** and adapter configuration
  under **`<Jellyfin data>/media-hub-discord/`**. Neither plugin
  directly opens/attaches/migrates Jellyfin databases or writes Jellyfin server/user/network settings.
  No replacement host assemblies, EF migrations, triggers or server-configuration overrides.
- Own schema migrations are transactional and additive. A database ownership marker and
  native-preview schema checks reject unrelated files; unknown future schemas and corrupt
  state disable the affected plugin functions rather than resetting data. Existing databases
  are checked read-only before any migration. Linked state directories/files are rejected.
- No persisted Jellyfin Web edits. Required Plugin Pages navigation is registered in memory
  and removed only for this plugin's GUID-scoped entry. No library refresh, account creation,
  media deletion, settings change or SyncPlay group change runs merely on install/start/stop.
- Stateful services initialize lazily behind plugin error boundaries. Discord startup,
  callbacks and bounded shutdown failures do not intentionally propagate to stop the host.
  The plugin does not change global host exception handling, host-service registrations, thread-pool settings
  or SQLite connection pools. Every plugin database connection is independently disposed.
- Uninstallation does not delete Jellyfin data/configuration or even the plugin's retained
  database/settings/key. Reinstallation can reuse compatible plugin state. Any future explicit
  reset must be separately confirmed and restricted to the plugin-owned directory.
- Future request/deletion features may affect media **only through explicit authorized user
  actions and supported service APIs**, not an installation, migration or uninstall hook.

Regression tests use host database/configuration fixtures and a real .NET generic host to
verify unchanged host files and fail-closed startup/shutdown with broken plugin state. A real
Jellyfin 12.2 local-ZIP install/restart/native-uninstall/reinstall smoke test now passes on an
  isolated Docker fixture for the historical monolith; see [ACCEPTANCE.md](ACCEPTANCE.md). This does not certify the split builds. Catalog/upgrade/provider/client
acceptance remains required. No in-process extension can
honestly guarantee zero possible host impact: assembly-loading, shared-runtime/resource faults,
Jellyfin's normal plugin bookkeeping and required restarts remain host-level concerns. These
  are reasons to test a preview first, not permission to modify host databases or settings.

Native UI follows Jellyfin Web component/label/container/list patterns with no independent
stylesheet. See [skin compatibility](SKINNING.md) for the conventions, Activity isolation and
remaining real-skin acceptance limits.

## Implemented

- Native top-bar watchlist navigation and a notification bell with unread count; a focused personal
  watchlist page, separate Add/Subscribe actions, removal-proposal markers and explicit Yes/No consent.
  The global bootstrap requires compatible File Transformation, and ordinary-user page navigation
  requires Plugin Pages. It reuses the visible Jellyfin 12 toolbar's themed classes, not an independent
  native stylesheet. Opening notifications never votes, marks updates read or changes native activity.
- Schema-13 complete-set activity/watchlist/request protection with private anti-rollback generations;
  retained unsigned watches require review, older request write plans stay read-only, and legacy
  removal activity is quarantined for a full configured recent-activity window. Restore matching
  whole-directory stopped backups rather than deleting authentication records.
- Schema 14 adds an encrypted, authenticated deletion-event journal without rewriting
  schema-13 roots or inventing earlier events. Private audit heads detect journal
  omission/replay. Interrupted root-publication/database-commit pairs fail closed,
  requiring a consistent stopped backup rather than automatic checkpoint promotion.
- Plugin file/database writes check a 256 MiB free-space reserve. This is a bounded
  best-effort guard, not a disk quota or a guarantee against external disk exhaustion.
- The adapter's first confirmed configuration save adds an authenticated custody
  marker without resetting existing roots/seals. Missing adapter state afterwards
  cannot reactivate legacy credentials. Core-native watchlists remain available when
  adapter configuration is corrupt. Back up/restore both plugin directories as a
  matching stopped snapshot; see [adapter migration](discord/README.md).

- Explicit item activity through `/hub-item`, `/hub-follow`, watchlist subscription and
  the authenticated item-activity API. Uses Jellyfin's timestamp-only user-data DTO;
  completion, progress, play count and favorites are not supplied or reset. No fake player.
  Bot interest is journaled in the plugin DB before the host update; a failed host write is
  reported without replay. Native player concurrency still needs real-host acceptance.
- Durable shared deletion proposals with default own-request authority and explicit,
  administrator-configurable denied/own-request/any-accessible-item account scopes.
  The any-item grant uses fresh native movie/whole-series provider/path identity,
  not fabricated Seerr request IDs. Live Seerr requester/Jellyfin identity and TMDB
  checks, native recent-activity scans (including series children), private consent inbox,
  agree/decline, owner-only Undo, anonymous viewer projections and a separate elevated audit
  endpoint. A supervised minute-cycle rechecks consent/aging while users are offline.
  New activity invalidates older votes. Personal delay increases extend existing queues;
  later reductions do not shorten their deadlines. **Automatic removal is off by default.**
  With an administrator's opt-in, the worker asks **Radarr/Sonarr file APIs** to remove the
  pinned file IDs; it never removes media files itself. Destination, edition/provider,
  native/Arr paths, entry-added timestamp and exact file identities are checked live.
  Ambiguous targets and partial-season requests that do not cover the whole show are blocked.
  Atomic execution claims, uncertain outcomes and a separate files-deleted checkpoint prevent
  deletion from being repeated during cleanup/restart. Monitoring-only editor/season-pass APIs
  unmonitor retained entries, seasons, episodes and future seasons without PUTting stale paths,
  profiles or tags. Real Arr/Jellyfin acceptance is still required before production activation.
  Consent notices appear in the authenticated page; opt-in Discord DMs have private, actor/activity-bound
  Agree/Decline controls. Ordinary queue payloads never include requester IDs or upstream pins.

- Native Jellyfin authentication, live user/token/session checks and administrator-only
  integration settings. Encrypted credentials are not returned by the settings API;
  optimistic revisions reject stale saves. No administrator API token impersonates viewers.
  Settings format 3 authenticates flags/destinations; older unsigned settings block integrations
  until an elevated review/save. Discovered Radarr/Sonarr endpoints require explicit approval
  of service, Seerr instance ID and exact URL. No approval means no Arr connection.
- `/hub-link` issues a private approval code and Discord ID. Explicitly approve it in an
  already signed-in Jellyfin browser. Existing Discord/Jellyfin bindings cannot be
  overwritten by a different account; retained credentials are verified live before disclosure.
  With a configured OAuth application, a private URL resumes sign-in and proves both browser
  identities using one-use state, optional PKCE and a Secure/HttpOnly callback cookie. Callback
  GET only records proof; a signed-in explicit POST confirms the displayed accounts. Forwarded
  authorization URLs without the original cookie cannot produce proof. Real-browser acceptance remains.
- With the Discord plugin installed, native watch parties use the public **Private SyncPlay SDK 1.1.0** with the separately installed
  **0.1.5** provider. Exact-player status, private creation with a one-use signed-in invitation,
  owner invitation/revocation and explicit leave/end are integrated without a duplicate engine.
  Missing/unhealthy providers fail safely; Activity and the old Discord invitation flow cannot
  impersonate a player. See [WATCHPARTY.md](WATCHPARTY.md) for bearer-link limits and acceptance gaps.
- Local per-user notification preferences, explicit Discord DM opt-in (off by default),
  global mute and a plugin-owned watchlist/subscriptions API and Jellyfin page. Subscribe
  adds membership atomically; unsubscribe keeps the title; Remove is separate. No favorites
  are changed. Current library visibility is checked on every read and subscription write.
  Revisions protect concurrent edits; late browser responses cannot replace newer state,
  change accounts or overwrite unsaved preference drafts. Movie/TV membership reconciles with
  the matching Seerr user's watchlist using a pinned mapping, complete stable reads, three-way
  baselines and durable revisioned intents. Unsubscribe retains membership. Ambiguous writes
  are read-reconciled, not blindly resent; reasserting a membership choice is an explicit new intent.
  Music/books remain plugin-local. Explicit Sync imports older accessible movie/series watches,
  preserving subscriptions and visible aliases; background updates do not import those watches.
  Uncertain sync review can adopt current Seerr membership locally without resending.
- User-scoped Seerr movie/TV discovery, search, details, request history, permission/quota
  projections and confirmed requests, plus native accessible movie/TV/music/book library browsing.
  Requests use `X-API-User` after live native/Seerr mapping and `auth/me` verification. Only
  allowlisted fields are returned. Seerr enforces the current permissions, quotas and defaults;
  the plugin does not supply an administrator requester/profile/destination override.
  Ten-minute actor-bound request plans are explicitly confirmed or cancelled before sending.
  Sending/uncertain requests are never automatically repeated, including after restart.
- Native Discord `/dashboard`, `/search`, `/discover`, `/library`, `/requests`, `/watchlist`,
  `/deletions`, `/notifications` and `/link` navigation. Opaque controls bind both identities,
  the exact private message and a ten-minute view lifetime; one click invalidates the old view.
  Navigation edits one response. Paginated poster galleries, watchlists/removals/updates, selected
  TV seasons, book/native editions and receipt refresh are connected. Seerr still owns request profiles.
- Required in-memory File Transformation bootstrap adds subscribe/proposal controls to supported
  Jellyfin Web cards and recognized native detail pages. No persistent web-file edits; no automatic mobile/TV injection is claimed.
  Movie/show proposal controls discover only the user's verified owned requests and require confirmation.
- Per-user availability/status feed with first-snapshot suppression, displayed-status deduplication,
  opt-in at-most-once Discord delivery, mute/access rechecks, read markers and bounded retention.
  A minute-cycle worker has a 45-second work budget and bounded reads; outages do not become
  availability transitions. Existing own Seerr requests and pinned local music/book requests have
  bounded rotating status scans. Optional HMAC notification-only wake-ups never trust payload states
  or replace polling. Personal and operator diagnostics expose scoped delivery counts.
- Real same-account native player destinations, explicit previews/confirmation and authenticated
  once-only command receipts (schema 12). Cancellation/receipt review never retries. Activity cannot
  invoke native player routes; command acceptance is not playback confirmation. Folders/series stay
  in the real Jellyfin player for individual-item selection. See [PLAYBACK.md](PLAYBACK.md).
- Explicit-load native/Activity PNG/JPEG posters use a fixed public artwork origin without credentials,
  redirects or relaxed CSP, with byte/dimension/concurrency limits and access revalidation.
- Versioned plugin-owned SQLite schema and restart persistence. Unknown future schemas are
  rejected rather than reset; native-preview upgrades retain records. Old incompatible native
  links/proofs require explicit reapproval, never silent replay. It never opens another
  application's database.
- Jellyfin components/classes only: no independent CSS, theme selector or chat theme system.
  **Jellyfin-12-compatible Plugin Pages and File Transformation are required for ordinary-user
  pages and invitation routes.** Standard Jellyfin configuration-page frontend routes are
  administrator-only. The elevated plugin configuration page works without these companions.
- Allowlisted ZIP packaging, real MD5/SHA-256 checksums, assembly GUID/version/API validation,
  a versioned Jellyfin catalog generator and GitHub release/Pages publication workflow.
- Lidarr/Readarr search, selected album/book-edition request plans, destination/profile/exclusion
  checks, daily per-user limits, durable receipts and local watches. Live storage/service projections
  omit keys and filesystem paths. No parent-wide search or existing-item modification is requested.
- Separately authorized operator messenger: rich private inbox metadata, attachment transfers,
  normal chat, explicitly previewed announcements, per-target uncertain-send receipts and retention.
  Limits are 8 MiB/file, four files and 16 MiB/send; only bounded PNG raster previews are enabled.
- Optional bundled Discord Activity uses short-lived memory-only OAuth sessions and shared services.
  It verifies application/scopes/current allowed-guild membership and the linked native account.
  It receives no Jellyfin credential and cannot configure the host or impersonate a player.
  A personal action copies only four validated color values from the native host theme; there is
  no cross-origin stylesheet proxy, arbitrary CSS or separate theme selector. Live SDK acceptance remains.
  See [ACTIVITY.md](ACTIVITY.md) for the callback, directory mapping and test setup.

## Remaining feature gates

| Feature | Status |
| --- | --- |
| Login-resuming linking | Dual-identity OAuth/cookie proof, explicit completion and opaque resume coded/tested; live browser acceptance remains |
| Private SyncPlay companion integration | Typed native creation, invites, owner moderation/policy and credential-free handoff implemented; live/concurrent-client acceptance remains |
| Native media catalog/requests/quotas | APIs, native/Activity artwork, Discord galleries, season/edition controls and receipt refresh implemented; live provider/client acceptance remains |
| Seerr identity and two-way watchlists | Scoped mapping, stable reads, visible aliases, legacy import and reviewed no-retry recovery implemented; live concurrency acceptance remains |
| Live storage, health and complete Arr discovery | Path/key-free projections implemented; real-service and broader UI acceptance remain |
| Lidarr/Readarr music/books requests and limits | Selected-item/edition plans, limits and receipts implemented; real-service acceptance remains |
| Personal watchlist subscription controls | Local/external API, page and supported Web card/detail controls implemented; other client/skin acceptance remains |
| Shared deletion queue and consent | Arr-only execution/cleanup, private inbox and opt-in DMs implemented; automatic removal defaults off; real-service acceptance remains |
| Notifications and hooks | Own requests/subscriptions, rotating music/book status, optional signed read-only hints and scoped diagnostics implemented; live signing-producer/provider acceptance remains |
| Rich private messenger, attachments, confirmed announcements, auditing/retention | Services, conversation/mention/retained-reply navigation and operator diagnostics implemented; live Discord acceptance remains |
| Real native player destinations | Own live capable sessions, explicit once-only previews/receipts and manual uncertainty review implemented; real-device/media acceptance remains |
| Discord Activity and validated cross-origin host-theme bridge | Bundled OAuth/shared-service adapter and color-only host bridge implemented; live SDK/proxy acceptance remains |

`FeatureParityComplete` remains false. Unit tests and an installable ZIP do not establish
real-host installation compatibility, native playback behavior or feature completeness.
No native TV/mobile controls are promised without those clients' own supported integrations.
See [FEATURES.md](FEATURES.md) and [SAFETY.md](SAFETY.md) for the remaining coding gates and operation boundaries.

## Install the core and the Discord plugin

These are **two separate plugin packages**, not two modes of a single installation.
Both target **Jellyfin 12.2.0 / .NET 10**. SDK/Node tools below are for development,
not an additional service you must install alongside Jellyfin.

| Package | Catalog / My Plugins name | Requirements |
| --- | --- | --- |
| `Jellyfin.Plugin.MediaHub_<core-version>.zip` | **Watchlist Deletion (Preview)** | Jellyfin 12.2; compatible File Transformation and Plugin Pages for native Web controls/pages |
| `Jellyfin.Plugin.MediaHub.Discord_<discord-version>.zip` | **Media Hub Discord Integration (Preview)** | The core with public API 1.0, plus separately installed Private SyncPlay 0.1.5 / API 1.1.0 |

The historical `MediaHub` core filename/GUID is retained for safe upgrades. The core
does **not** bundle Discord. Bundled API/SDK DLLs are dependencies, not extra project
plugins to install independently. The Private SyncPlay **provider** is a separate
third-party installation; its bundled API DLL alone does not install the provider.

### Install Watchlist Deletion only

1. Install Jellyfin-12.2-compatible **File Transformation**, then **Plugin Pages**,
   using the [dependency guide](WEB_DEPENDENCIES.md).
2. Install **Watchlist Deletion (Preview)** through the catalog when published, or
   install its full core ZIP manually using the instructions linked below. Restart Jellyfin.
3. Open **Dashboard → Plugins → My Plugins → Watchlist Deletion (Preview) → Settings**.
   Configure existing Jellyfin/Seerr services and explicit Arr destination approvals;
   use the separate deletion-permission editor for default/individual account scopes.
4. Leave automatic removal off during setup and verify the native queue, notifications
   and user access first. Users use normal Jellyfin accounts.

**This installation needs no Discord bot, token, account link or Private SyncPlay.**

### Add Discord Integration

1. Install the core first.
2. Separately install the reviewed **Private SyncPlay 0.1.5 / public API 1.1.0** provider
   following [its upstream instructions](https://github.com/someRandomDude-a/private-syncplay-groups).
3. Install the separate **Media Hub Discord Integration (Preview)** ZIP/catalog entry.
   Keep its files separate from the core; restart Jellyfin and verify both plugins are Active.
4. Configure the existing public Jellyfin HTTPS base URL in the core. Then open
   **Media Hub Discord Integration (Preview) → Settings**, not the core's retained
   legacy Discord fields. Set the bot token and explicit server allowlist. Configure
   the matching Discord application ID/OAuth secret when using OAuth linking or Activity;
   see [Activity setup](ACTIVITY.md). Save explicitly, then enable the desired features.
5. Link each user's own accounts through `/hub-link` and explicit native approval.
   Do not grant the bot Administrator or reuse its token in another running instance.

The adapter reads no development `.env` at runtime. Enter secrets through its native
administrator configuration page; do not copy credentials, source trees or test files
into Jellyfin's plugins directory. Missing dependencies leave gateway/player controls unavailable.

### Catalog availability and manual ZIPs

**These source changes do not publish either package.** The catalog publication target is:

```text
https://somerandomdude-a.github.io/seerr-discord-bot/manifest.json
```

Add it under **Dashboard → Plugins → Repositories** only after verifying the manifest
and referenced ZIPs exist. Each published plugin is a separate **Catalog** entry.
The shared **Publish Jellyfin plugin repository** workflow builds and publishes both
plugins to this one catalog. Add the repository once, then install each desired entry
separately. Dependencies are not installed automatically. Until an authorized successful
run of the updated publisher, live entries may still be older or missing.
Discord CI also offers an unpublished **discord-integration-development-only** artifact
for deliberate manual preview testing. A legacy all-in-one release is not a split package.

For manual extraction, stopping/starting Jellyfin, versioned folder examples and how
to unpack the outer Actions artifact, follow the
[main README ZIP-install instructions](../README.md#catalog-installation-versus-manual-zip-installation).
Extract the complete core ZIP to `MediaHub_<core-version>/` and the complete adapter
ZIP to `MediaHub.Discord_<discord-version>/`, each inside Jellyfin's configured
`plugins` directory. Do not merge folders, deduplicate SDK DLLs across plugins,
or copy host assemblies. Validate each ZIP's `SHA256SUMS` first.

The two release numbers need not be equal, but both must explicitly support Jellyfin
12.2 and the same core API contract. Do not install an arbitrary older core/adapter.

### Upgrade from the monolith or an earlier split build

Pause the bot and automatic removal explicitly before maintenance. Stop Jellyfin and
back up the complete `media-hub/` and, if present, `media-hub-discord/` state directories
as a matching snapshot, including databases/WAL, keys, settings and authentication roots.
Upgrade the existing core identity; never erase or recreate its data directory. Do not
load the old monolith beside the split core. Use Jellyfin's supported upgrade flow,
or retain old **binary** folders outside the scanned plugin directory during manual replacement.

After installing the selected core and optional adapter/dependencies, restart. In the
core settings, explicitly review/save any legacy configuration flagged for review first.
If installing the adapter, then review **Discord Integration → Settings**. Its first confirmed save/import migrates
configuration ownership, not the database, links or history. Do not copy the core's
database/key into the adapter directory. Verify retained state before enabling anything.
Missing/corrupt state or a downgrade requires matching stopped backups, not deleting
keys or promoting an isolated previous file. See [adapter migration](discord/README.md#configuration-ownership-and-safe-migration).

The plugin uses its hosting instance's native services, not a separately configured internal
Jellyfin API key. Seerr/Arr services remain external. No Docker/network settings or DNS are
changed and no new public/LAN port is needed.

## Build and test

### Which workflow builds which plugin?

| Workflow file | GitHub Actions name | Output / installation behavior |
| --- | --- | --- |
| `.github/workflows/plugin.yml` | **Media Hub plugin CI** | Compiles/tests the core and Web code; no server deployment |
| `.github/workflows/plugin-release.yml` | **Publish Jellyfin plugin repository** | Builds/tests/packages both separate plugins, checks disposable acceptance servers, verifies both prerelease downloads, then publishes one combined catalog |
| `.github/workflows/plugin-discord.yml` | **Discord integration CI** | Separately tests/builds/packages the adapter; download **discord-integration-development-only** from the completed run's Artifacts section; no release/catalog publication |

No workflow installs either plugin onto your own Jellyfin server. Use the catalog
or manual ZIP steps above. One shared publication run releases both plugins;
the separate Discord CI workflow remains build/test-only.

Requires **.NET 10 SDK** and **Node 22.12+** for browser tests and the bundled Activity build.
The installed plugin needs its Jellyfin host runtime. Only the latest stable Jellyfin, currently
12.2.0, is targeted; older/unreviewed API overrides are rejected. Watch parties require
the Discord plugin and matching Private SyncPlay provider. Review the pinned API/SDK/catalog ABI together when
a new stable Jellyfin is released; no floating dependency or older-version fallback is used.
CI/release checks compare the reviewed release tag with Jellyfin's latest stable release and fail
closed on drift or lookup failure instead of silently publishing an older target or auto-upgrading it.

```sh
dotnet test plugin/tests/MediaHub.Tests/MediaHub.Tests.csproj -c Release
dotnet test plugin/tests/MediaHub.Tests/MediaHub.Tests.csproj -c Release -p:JellyfinVersion=12.2.0
npm ci --prefix plugin/web
npm test --prefix plugin/web
npm ci --ignore-scripts --prefix plugin/activity
npm run build --prefix plugin/activity
dotnet test plugin/tests/MediaHub.Discord.Tests/MediaHub.Discord.Tests.csproj -c Release -p:RequireActivityBundle=true
```

Build the **core** preview (no Activity/Node build is required for this assembly).
Use new/empty output directories and substitute the intended version when creating a release:

```sh
dotnet build plugin/src/Jellyfin.Plugin.MediaHub/Jellyfin.Plugin.MediaHub.csproj -c Release -p:Version=0.1.0.0 -p:AssemblyVersion=0.1.0.0 -p:FileVersion=0.1.0.0 -p:InformationalVersion=0.1.0.0 -o plugin/artifacts/core-build
dotnet run --project plugin/tools/MediaHub.Packager -c Release -- --plugin core --input plugin/artifacts/core-build --output plugin/artifacts/core-repository --metadata plugin/repository/plugin.json --repository someRandomDude-a/seerr-discord-bot --version 0.1.0.0
```

Build the **separate Discord plugin** preview, including its Activity assets:

```sh
npm ci --ignore-scripts --prefix plugin/activity
npm run build --prefix plugin/activity
dotnet build plugin/src/Jellyfin.Plugin.MediaHub.Discord/Jellyfin.Plugin.MediaHub.Discord.csproj -c Release -p:RequireActivityBundle=true -p:Version=0.1.0.0 -p:AssemblyVersion=0.1.0.0 -p:FileVersion=0.1.0.0 -p:InformationalVersion=0.1.0.0 -o plugin/artifacts/discord-build
dotnet run --project plugin/tools/MediaHub.Packager -c Release -- --plugin discord --input plugin/artifacts/discord-build --output plugin/artifacts/discord-repository --metadata plugin/repository/discord.json --repository someRandomDude-a/seerr-discord-bot --version 0.1.0.0
```

Output: versioned ZIP, `manifest.json`, `SHA256SUMS` and a catalog landing page. Local generation
does **not** upload the ZIP; its `sourceUrl` becomes usable only after that immutable release
exists. Pass `--previous <downloaded-manifest.json>` for subsequent releases. Existing versions
cannot be overwritten and each new version must increase. Only allowlisted runtime files and
license notices are zipped: no host DLLs, secrets, tests, PDBs or developer-machine native assets.
Do not install the test application's build output into Jellyfin.

For the **shared catalog**, use both build directories above and a new empty output directory:

```sh
dotnet run --project plugin/tools/MediaHub.Packager -c Release -- --plugin both --input plugin/artifacts/core-build --discord-input plugin/artifacts/discord-build --output plugin/artifacts/repository --metadata plugin/repository/plugin.json --discord-metadata plugin/repository/discord.json --repository someRandomDude-a/seerr-discord-bot --version 0.1.0.0
```

This produces two separate ZIPs, a two-entry manifest, combined `SHA256SUMS` and landing page.
For an existing repository, pass `--history <directory>` containing downloaded authoritative
release snapshots named `manifest.json` in separate subdirectories. Conflicting immutable
version records are rejected; neither plugin's older versions are dropped. Do not deploy a
single-plugin development manifest over the shared catalog.

### Publish the repository

These steps publish **both separate plugins in one catalog**, using the same new
four-part version for this publication run. The core remains installable without Discord.
Publishing does not enable the bot or automatic removal, or certify production readiness.

1. Commit/push the plugin and workflows. Enable repository Actions, permit release writes,
   and set **Settings → Pages → Source → GitHub Actions**. Review the `github-pages` environment
   protection settings. Do not replace an existing unrelated Pages site without deciding how
   to host both; this workflow owns the Pages root.
2. Run **Publish Jellyfin plugin repository** with a new four-part version (for example
   `0.1.0.0`), or push its `plugin-v0.1.0.0` tag.
3. The workflow tests both plugins and the browser suite against the reviewed stable API,
   stamps/builds both assemblies, and merges history from the highest published core and
   Discord release snapshots. It checks both exact ZIPs, creates separate draft **prereleases**
   (`plugin-v…` and `discord-v…`), downloads/verifies both packages and their shared manifest,
   publishes both releases, then deploys Pages. Missing/corrupt/conflicting history or either
   failed download blocks catalog deployment, even if Pages is unavailable.
4. Verify both live manifest entries and install each desired entry through the catalog on a test Jellyfin. Upstream-host
   dependency resolution, first install, upgrade/restart and web routing still require this test.

If publication or Pages fails, rerun only the failed release/deployment jobs using their
existing artifacts. The publisher resumes missing draft assets only, rejects different existing
bytes, and never overwrites published assets. Do not rebuild the same version or delete its assets to retry;
if artifacts expired, publish a higher version, which retains the preceding release history.

## State and manual acceptance

State lives at `<Jellyfin data>/media-hub/`: `hub.db`/WAL/SHM, encrypted `settings.json` and
`secrets.key`. Back up the whole directory consistently. Losing the key blocks access rather
than replacing ciphertext. Protect Windows ACLs; Unix directory/key/settings are private.
Schema 11 retains old deletion records without authenticating their historical consent/deadlines;
pre-send proposals need owner Undo and a new explicit proposal. See the upgrade implications and
remaining adversarial-state/rollback limitations in [SECURITY_REVIEW.md](SECURITY_REVIEW.md).

### Persistence and recovery safeguards

- First database creation builds/commits a private staging database before publishing
  `hub.db`. Interrupted creation cannot leave an empty database at the live path.
- Startup checks database ownership, the known schema, SQLite `quick_check` and explicit
  revision/subscription invariants before upgrading. Each migration first makes a verified, SQLite-online-backup snapshot under
  `media-hub/backups/`, including committed WAL data. Backup failure aborts the upgrade.
  Snapshots are content-addressed: identical failed-upgrade retries do not fill the disk
  with duplicates. No automatic snapshot deletion or restore occurs.
- Migration schema/version changes commit together. Plugin connections explicitly use
  SQLite `synchronous=FULL`, foreign-key enforcement and `trusted_schema=OFF`. No host
  connection policies are changed. Write contention is bounded; writes are not blindly replayed.
- Settings and initial keys are flushed to private sibling staging files before rename.
  Settings replacement keeps one `settings.previous.json` with the previous encrypted
  credentials. Shared file leases and on-disk revisions prevent competing service instances
  from silently overwriting a newer save. Lease files contain no credentials and are harmless
  after a process exits; do not delete an active lock file to bypass a writer.
- Settings have a format version and bounded reads. Unknown/newer fields, incomplete or
  duplicate JSON, oversized files and invalid ciphertext fail closed rather than being
  rewritten with defaults. Missing/changed keys are rechecked during live operations;
  no saved key is silently regenerated. Broken state stops the Discord connection with
  bounded retry backoff, not Jellyfin.

Fault-injection tests simulate interrupted creation/migration/key/settings publication,
failed backup storage, concurrent writers and transaction rollback. They do **not** prove
survival of every filesystem fault or actual power loss. Use reliable local storage for
SQLite/WAL (not an unsupported network filesystem), monitor free space and keep consistent
off-machine backups. The automatic pre-upgrade **database snapshot is not a full backup**:
encrypted rows still require their matching `secrets.key` and settings.

For manual recovery, stop Jellyfin during the maintenance window, preserve a copy of the
damaged plugin directory, then restore a consistent Media Hub database/settings/key set.
Never replace Jellyfin's own database or server/network configuration. A `.db` copy made
while WAL writes are active is not a consistent backup; use a stopped copy of the whole
state or a verified SQLite backup. Do not mix keys or blindly promote `.tmp` files. Restart
and verify access/state before resuming explicit actions; no automatic reset, rollback or
uncertain external-operation replay is performed by the plugin.

- Test ordinary/admin users, modern/legacy Jellyfin Web, Plugin Pages, base paths and themes.
- Approve a self-requested link, restart, revoke/logout and disable/delete the account; failed
  live verification must block private data. Do not approve codes forwarded by other people.
- Test per-user isolation, bell on/off versus Remove, mute, stale tabs, drafts, account changes,
   missing/restricted items and restart. Validate optional Web-card transformation, native
   movie/TV editions, complete Seerr pagination and stale-message rejection on actual clients.
- Create in an actual player, start test content, invite another account and verify native
   synchronized pause/seek/buffering after an exact-session verifier exists. Until then,
   invitation publication/join must be blocked. Test wrong recipients/guilds/channels/accounts, expired
  or reused links, lost library access, host-player changes and Jellyfin/group restart.
- Closing invitations must not eject viewers. Ambiguous create/send/join writes are never
  automatically replayed. Complete all remaining gates before production activation.
