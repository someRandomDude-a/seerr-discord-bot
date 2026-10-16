# Media Hub for Jellyfin 12 · new plugin preview

Media Hub is an **in-process C#/.NET 10 plugin** for Jellyfin 12. It uses Jellyfin's existing
authenticated pages and listener, with no additional runtime, sidecar or admin port.
It is **not yet a feature-complete replacement**. The [implementation plan](IMPLEMENTATION_PLAN.md)
tracks the remaining Discord, Activity, media and safety features.

**First installation? Open Media Hub's settings as the Jellyfin administrator.** The integrated
**Set up Media Hub** tutorial opens automatically, walks through one step at a time and places the
actual configuration forms in the relevant steps. It covers Discord app creation, IDs/secrets,
least-privilege permissions, account linking, optional inbox intents and web Activity setup.
**Pause / Resume / Restart tutorial** remains available at any time without resetting settings or
links; only explicit Save actions change configuration. Progress is browser-local, scoped to the
native user/server/page, and stores no credentials or form drafts. The [onboarding reference](ONBOARDING.md)
is supplementary, not required reading before setup.

**Fresh installation:** users create their own links/settings. Existing upstream Seerr requests
are retained, not automatically deleted or recreated.
Evaluate this preview on a test Jellyfin with a separate test Discord application.
Use one plugin instance per Discord application token.

## Jellyfin safety boundary

- All plugin state is isolated under **`<Jellyfin data>/media-hub/`**. The plugin does not
  directly open/attach/migrate Jellyfin databases or write Jellyfin server/user/network settings.
  No replacement host assemblies, EF migrations, triggers or server-configuration overrides.
- Own schema migrations are transactional and additive. A database ownership marker and
  native-preview schema checks reject unrelated files; unknown future schemas and corrupt
  state disable the affected plugin functions rather than resetting data. Existing databases
  are checked read-only before any migration. Linked state directories/files are rejected.
- No persisted Jellyfin Web edits. Optional Plugin Pages navigation is registered in memory
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
isolated Docker fixture; see [ACCEPTANCE.md](ACCEPTANCE.md). Catalog/upgrade/provider/client
acceptance remains required. No in-process extension can
honestly guarantee zero possible host impact: assembly-loading, shared-runtime/resource faults,
Jellyfin's normal plugin bookkeeping and required restarts remain host-level concerns. These
are reasons to test a preview first, not permission to modify host databases or settings.

## Implemented

- Explicit item activity through `/hub-item`, `/hub-follow`, watchlist subscription and
  the authenticated item-activity API. Uses Jellyfin's timestamp-only user-data DTO;
  completion, progress, play count and favorites are not supplied or reset. No fake player.
  Bot interest is journaled in the plugin DB before the host update; a failed host write is
  reported without replay. Native player concurrency still needs real-host acceptance.
- Durable shared deletion proposals with live Seerr requester/Jellyfin identity and TMDB
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
- Initial SyncPlay invitation plumbing: the creator's actual authenticated browser session creates
  the native group. `/watchparty` posts a generic server/channel join button;
  `/watchparty recipient:USER` sends a generic DM. Each click rechecks the audience and
  linked account before privately issuing a two-minute, account-bound, one-use join URL.
- Joining targets the opening browser's real session and native library/SyncPlay policy.
  The UI waits for native `GroupJoined`, not just HTTP success. Closing invitations does
  not destroy a native group or eject participants. Invitations fail closed after native
  groups disappear. **Exact creator-session membership still needs hardening**; a participant
  username alone does not prove the original player remains joined. **Invitations now fail
  closed without an exact-session membership verifier.** Jellyfin 12's public group DTO does
  not provide this proof; no production verifier is registered and native SyncPlay remains usable.
- Local per-user notification preferences, explicit Discord DM opt-in (off by default),
  global mute and a plugin-owned watchlist/subscriptions API and Jellyfin page. Subscribe
  adds membership atomically; unsubscribe keeps the title; Remove is separate. No favorites
  are changed. Current library visibility is checked on every read and subscription write.
  Revisions protect concurrent edits; late browser responses cannot replace newer state,
  change accounts or overwrite unsaved preference drafts. Movie/TV membership reconciles with
  the matching Seerr user's watchlist using a pinned mapping, complete stable reads, three-way
  baselines and durable revisioned intents. Unsubscribe retains membership. Ambiguous writes
  are read-reconciled, not blindly resent; reasserting a membership choice is an explicit new intent.
  Music/books remain plugin-local. Existing native-preview watches made before this increment
  need their bell reasserted to create an upstream intent.
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
  Navigation edits one response. Full rich poster galleries, all watchlist pages and advanced
  season/profile selection in Discord still need completion; Jellyfin pages support season selection.
- Optional in-memory File Transformation bootstrap adds subscribe/proposal controls to supported
  Jellyfin Web cards. No persistent web-file edits; no automatic mobile/TV injection is claimed.
  Movie/show proposal controls discover only the user's verified owned requests and require confirmation.
- Per-user availability/status feed with first-snapshot suppression, displayed-status deduplication,
  opt-in at-most-once Discord delivery, mute/access rechecks, read markers and bounded retention.
  A minute-cycle worker has a 45-second work budget and bounded reads; outages do not become
  availability transitions. This is polling, not a completed webhook/music/book notification port.
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
| Real-browser/Discord SyncPlay acceptance and exact host-player ownership | Invitations blocked without supported exact-session proof |
| Native media catalog/requests/quotas | Movie/TV API, Jellyfin page and initial private Discord controls implemented; rich galleries/advanced defaults remain |
| Seerr identity and two-way watchlists | Scoped verified mapping, stable reads and durable reconciliation implemented; multi-edition/card reconciliation needs broader coverage |
| Live storage, health and complete Arr discovery | Path/key-free projections implemented; real-service and broader UI acceptance remain |
| Lidarr/Readarr music/books requests and limits | Selected-item/edition plans, limits and receipts implemented; real-service acceptance remains |
| Personal watchlist subscription controls | Local/external API, page and supported Web-card controls implemented; native detail/mobile/TV extension coverage remains |
| Shared deletion queue and consent | Arr-only execution/cleanup, private inbox and opt-in DMs implemented; automatic removal defaults off; real-service acceptance remains |
| Notifications and hooks | Availability/status polling/feed and opt-in Discord sends implemented; hooks and full request/music/book coverage remain |
| Rich private messenger, attachments, confirmed announcements, auditing/retention | Core services/native UI implemented; live Discord acceptance and navigation polish remain |
| Discord Activity and validated cross-origin host-theme bridge | Bundled OAuth/shared-service adapter and color-only host bridge implemented; live SDK/proxy acceptance remains |

`FeatureParityComplete` remains false. Unit tests and an installable ZIP do not establish
real-host installation compatibility, native playback behavior or feature completeness.
No native TV/mobile controls are promised without those clients' own supported integrations.
See [FEATURES.md](FEATURES.md) and [SAFETY.md](SAFETY.md) for the remaining coding gates and operation boundaries.

## Install from the plugin repository

**The repository is not published by these source changes.** After the owner runs the release
workflow successfully, its default GitHub Pages catalog URL will be:

```text
https://somerandomdude-a.github.io/seerr-discord-bot/manifest.json
```

1. Confirm the HTTPS URL returns the catalog JSON and its referenced release ZIP exists.
2. In Jellyfin **Dashboard → Plugins → Repositories**, add that manifest URL (not the Git URL).
3. Install **Media Hub (Preview)** from the catalog on a test Jellyfin 12 host, then restart.
4. Install compatible Plugin Pages/File Transformation for the ordinary-user page.
5. Configure the external Jellyfin HTTPS base URL (including any base path), allowed guild
   IDs and a **test** Discord token in the elevated settings section. Discord is off by default.

The plugin uses its hosting instance's native services, not a separately configured internal
Jellyfin API key. Seerr/Arr services remain external. No Docker/network settings or DNS are
changed and no new public/LAN port is needed.

## Build and test

Requires **.NET 10 SDK** and **Node 22.12+** for browser tests and the bundled Activity build.
The installed plugin needs only its Jellyfin host runtime. The default API baseline is Jellyfin 12.0.0.

```sh
dotnet test plugin/tests/MediaHub.Tests/MediaHub.Tests.csproj -c Release
dotnet test plugin/tests/MediaHub.Tests/MediaHub.Tests.csproj -c Release -p:JellyfinVersion=12.2.0
npm ci --prefix plugin/web
npm test --prefix plugin/web
npm ci --ignore-scripts --prefix plugin/activity
npm run build --prefix plugin/activity
```

Build a local preview package (use a new/empty repository output directory each time):

```sh
dotnet build plugin/src/Jellyfin.Plugin.MediaHub/Jellyfin.Plugin.MediaHub.csproj -c Release -p:JellyfinVersion=12.0.0 -p:RequireActivityBundle=true -p:Version=0.1.0.0 -p:AssemblyVersion=0.1.0.0 -p:FileVersion=0.1.0.0 -p:InformationalVersion=0.1.0.0 -o plugin/artifacts/build
dotnet run --project plugin/tools/MediaHub.Packager -c Release -- --input plugin/artifacts/build --output plugin/artifacts/repository --metadata plugin/repository/plugin.json --repository someRandomDude-a/seerr-discord-bot --version 0.1.0.0
```

Output: versioned ZIP, `manifest.json`, `SHA256SUMS` and a catalog landing page. Local generation
does **not** upload the ZIP; its `sourceUrl` becomes usable only after that immutable release
exists. Pass `--previous <downloaded-manifest.json>` for subsequent releases. Existing versions
cannot be overwritten and each new version must increase. Only allowlisted runtime files and
license notices are zipped: no host DLLs, secrets, tests, PDBs or developer-machine native assets.
Do not install the test application's build output into Jellyfin.

### Publish the repository

1. Commit/push the plugin and workflows. Enable repository Actions, permit release writes,
   and set **Settings → Pages → Source → GitHub Actions**. Review the `github-pages` environment
   protection settings. Do not replace an existing unrelated Pages site without deciding how
   to host both; this workflow owns the Pages root.
2. Run **Publish Jellyfin plugin repository** with a new four-part version (for example
   `0.1.0.0`), or push its `plugin-v0.1.0.0` tag.
3. The workflow tests both API versions and the independent browser suite, stamps/builds the
   baseline assembly, preserves catalog history from the highest published plugin release,
   creates an immutable **prerelease**, downloads/verifies its ZIP and then deploys Pages.
   Missing/corrupt prior history fails closed, even if Pages is unavailable.
4. Verify the live manifest and install its downloaded ZIP on a test Jellyfin. Upstream-host
   dependency resolution, first install, upgrade/restart and web routing still require this test.

If Pages fails after release creation, rerun the failed repository deployment job using its
existing artifacts. Do not overwrite the published version or delete its assets to retry;
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
