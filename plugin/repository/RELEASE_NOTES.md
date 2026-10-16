## Media Hub plugin preview

Media Hub runs inside Jellyfin. Users start with their own links and settings; existing upstream
requests are preserved.

On first installation, the integrated **Set up Media Hub** tutorial automatically opens for the
Jellyfin administrator, showing one step at a time with the actual configuration forms. It covers app creation,
separate secrets/IDs, limited permissions and optional intents, both configuration forms,
account linking, and the web Activity's redirects/directory mapping/App Launcher. Back/Next,
Pause/Resume and Restart tutorial never reset settings or enable features. Browser-local progress
contains no credentials/drafts; only explicit Save actions change configuration.
`plugin/ONBOARDING.md` remains an optional reference.

Media Hub owns only `<Jellyfin data>/media-hub/`. It does not directly modify Jellyfin
database schemas/configuration or persist web-file edits. Corrupt/foreign/future plugin
state fails closed; startup/shutdown do not reset host data. Uninstallation retains
plugin state and does not clean up Jellyfin data/settings. Generic-host isolation tests
pass. Local-ZIP installation, real restart, native uninstall/restart and retained-state reinstall
also pass on an isolated official Jellyfin 12.2 fixture; see `plugin/ACCEPTANCE.md` for scope.
Published-catalog, real upgrade/provider/client acceptance is still required.
Persistence hardening adds staged database/key creation, verified pre-upgrade SQLite
snapshots, explicit durability settings, atomic settings replacement with a previous
version, shared save leases and interruption/concurrency regression tests. Recovery is
manual; there is no automatic reset or rollback of retained state.

Adversarial-input hardening adds bounded/duplicate-safe JSON, plugin-only request/event budgets,
strict service addresses and independent administrator approval of every discovered Arr
service/instance/address. Settings format 3 authenticates integration flags and destinations;
older unsigned settings require elevated review and an explicit save before integration use.
Schema 11 authenticates new deletion proposals/deadlines/votes but **does not approve old
consent during migration**. Retained records are not reset. Old native links and incompatible
request/OAuth/control proofs need explicit reapproval/new previews. Message previews, attachment
digests and per-target send states are authenticated; uncertain outcomes remain non-retryable.
See `plugin/SECURITY_REVIEW.md` for scope, upgrade implications and remaining state/rollback
trust assumptions. These changes do not establish production readiness.

The current preview includes native account approval, elevated settings, movie/TV catalog
and confirmed user-scoped Seerr requests, native library browsing, local/external per-user
watchlists, stable two-way Seerr membership reconciliation, status feeds and opt-in Discord
notifications. Initial Discord media navigation uses exact-message/actor-bound expiring
controls. Optional Web-card controls transform only served responses, not host files.

The preview now includes timestamp-only Jellyfin activity updates, initial `/hub-item`
and `/hub-follow` commands, live Seerr proposal-ownership checks, an anonymous shared
proposal queue, private consent inbox/opt-in DMs and owner-only Undo. Automatic removal
**defaults off**. Once explicitly enabled, the worker uses only pinned Radarr/Sonarr file-ID
APIs, never direct media filesystem deletion. Durable claims, uncertain outcomes and the
files-deleted checkpoint separate deletion from restart-safe monitoring-only cleanup.
Sonarr cleanup does not PUT stale paths/profiles/tags over an entire series record.

Dual-identity OAuth linking/login resume, selected Lidarr/Readarr music/book requests,
storage projections, operator messenger/files/confirmed announcements and an optional
bundled OAuth Activity/shared-service adapter are now coded with automated tests. They
still need real provider/browser/Discord acceptance; see `plugin/ACTIVITY.md` for test setup.

This is **not feature-complete**. Rich galleries/navigation, request/notification hooks,
watchlist reconciliation edge cases and live Activity acceptance remain incomplete.
SyncPlay invitation plumbing is gated off without exact original
creator-session membership proof; participant usernames alone do not suffice. Native
concurrent-playback, real Arr operations and published-catalog/upgrade acceptance are outstanding.

Use a test Jellyfin 12 instance and a separate test Discord application for evaluation.
Plugin Pages and File Transformation need compatible Jellyfin 12 builds for ordinary-user
pages and automatic invitation links. Native host/browser/Discord acceptance is still required.

Install from the repository manifest in Jellyfin's plugin catalog, then restart Jellyfin.
Use one plugin instance per Discord application token.
