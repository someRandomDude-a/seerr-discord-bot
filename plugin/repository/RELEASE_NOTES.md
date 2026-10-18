## Media Hub plugin preview

Media Hub runs inside Jellyfin. Users start with their own links and settings; existing upstream
requests are preserved.

Personal navigation adds a themed top-bar notification bell/unread count and watchlist entry,
separate Add/Subscribe controls, visible removal-proposal markers and explicit Yes/No consent.
Consent now reads **Allow deletion?** with native primary/neutral buttons and a Help icon for
the full rules. There is no independent color palette to override Jellyfin skins.
Global Web integration requires compatible File Transformation/Plugin Pages; unsupported clients
keep native behavior. No vote/read marker or native activity is implied by viewing notifications.
Dependency discovery follows upstream/Home Screen Sections reflection and minimum/API checks;
exact archive/hash pins are for disposable acceptance and CI only, not runtime version equality.

Schema 13 protects complete activity/watchlist/request sets with private outside-SQLite generations,
including omissions, consumed states and replayed former seals. Existing unsigned activity is
quarantined for a full recent-activity window, retained watches need explicit sync review, and old
request plans are read-only. Preserve and restore the entire stopped plugin directory, including
`state-auth.json`, the matching database/key/settings and retained checkpoints. Never reset one file
to bypass an integrity failure. Discord adds owned-request removal proposals, sync recovery,
independent preferences/quotas, player history/review and richer authorized inbox controls.

Release gates now test the bundled release-version assembly and exact installed ZIP/native browser,
use SHA-pinned actions and check dependency advisories. Live Discord/services/provider/theme and real
GitHub-runner acceptance remain required; `FeatureParityComplete` is still false.

Administrator setup uses a conventional **Server connections and permissions** workspace, with
section/mode dropdowns, contextual Help, expandable Discord/OAuth details, named saved-service
folder/profile choices and a structured destination-approval list. One explicit save covers every
section; navigation never saves or auto-enables a feature. Secret-free draft review, confirmed reload
and explicit credential removal/disconnect replace the walkthrough. No draft/progress browser storage.
Native request quotas, Seerr links, read-only receipt refresh and administrator removal-audit display
are exposed without broadening request, messaging or deletion authority. See `plugin/ONBOARDING.md`.

The implementation-gap pass adds visible watchlist aliases/legacy import and no-retry reviewed
recovery, paginated Discord galleries/seasons/editions/removals/updates, native/Activity bounded
explicit-load artwork, own-request notifications, optional HMAC read-only hints, scoped diagnostics,
messenger mention/conversation/retained-reply navigation, recognized Web detail controls and owner-only
Private SyncPlay policy/moderation. Native/Discord handoff carries no party bearer secret.
Schema 12 adds an authenticated once-only native player-command journal for real same-account
capable players, with explicit previews, unsent cancellation and manual uncertainty review.
Command acceptance is never playback confirmation; Activity cannot invoke native player routes.
Provider/client/real-media acceptance still blocks feature-complete/production-ready claims.

Stock Jellyfin UI acceptance identified a native-component compatibility issue: dynamic fields now
use parsed markup inside their containers, and buttons/checkboxes use Jellyfin's v0 creation API.
This preserves Jellyfin-generated labels without constructing parentless upgraded fields or using
unsupported v1 options. Regression coverage exercises the v0 contract; no native theme is added.

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
Native watch parties use the matching public Private SyncPlay API 1.1 / 0.1.5 provider, installed
separately. Private creation, one-use signed-in invitations, revocation and explicit leave/end use
real player identities; playback/admission remain in the companion. Old party records are retained,
but old routes/buttons cannot redeem invitations. Guests and old Discord publishing are not enabled. Native
concurrent-playback, real Arr operations and published-catalog/upgrade acceptance are outstanding.

Only latest stable Jellyfin **12.2.0** is targeted by this source/build/catalog. Older releases are
not supported; CI/release checks block on stable-release drift until the pins are reviewed together.
Use a test Jellyfin 12.2 instance and a separate test Discord application for evaluation.
Plugin Pages and File Transformation need compatible Jellyfin 12 builds for ordinary-user
pages and linking. Native host/browser/Discord acceptance is still required.

Install from the repository manifest in Jellyfin's plugin catalog, then restart Jellyfin.
Use one plugin instance per Discord application token.
