# Plugin feature checklist

This tracks the Media Hub plugin's implemented features and remaining work, **not a release or
server-testing readiness claim**. Native pages, Discord commands and the bundled Activity share
the same application services and permission checks. The existing API readiness field
`FeatureParityComplete` stays false until every feature and safety gate is complete.

## Connected, with automated tests

- Plugin-only persistence (schema 13), additive upgrades/snapshots, matching authenticated settings/key,
  failure-contained lifecycle and retained state on uninstall.
- Conventional administrator settings workspace: section/mode dropdowns, contextual Help, expandable
  Discord/OAuth connection details, one explicit save for all groups, secret-free change review and
  confirmed reload. No wizard or browser-stored drafts/progress. Explicit credential removal/disconnect
  preserves accounts/history/media. Named saved-service folders/profiles and structured, independently
  approved Seerr destinations replace numeric profile entry and raw JSON.
- Native browser identity, explicit Discord code approval and live linked-token checks. OAuth
  handoffs add dual-identity proof, state/optional PKCE, a callback-scoped Secure/HttpOnly/Lax cookie,
  explicit authenticated completion and opaque-handoff-only login resume.
- Unique pinned Seerr/Jellyfin mapping, verified `X-API-User` permissions, quotas, movie/TV
  discovery/search/details/history, request plans/confirmation/cancellation and restart-safe receipts.
- Accessible native movie/TV/music/book library browsing, explicit timestamp-only activity;
  GET/list/poll/notification operations do not simulate playback or modify native user data.
- Local watches plus external movie/TV membership, three-way baseline reconciliation,
  stable complete pagination, revisioned intents and no blind uncertain-write replay.
- Personal preferences, mute/channel opt-ins, Jellyfin feed/read markers, status polling and
  at-most-once Discord availability/consent delivery with live access rechecks.
- Native Discord media navigation and message/actor-bound expiring controls. Jellyfin catalog
  and season selection. Optional response-only Web-card subscription/deletion controls.
- Native explicit request permissions/quota display, safe Seerr navigation, read-only request receipt
  refresh/unsent cancellation and separately authorized administrator removal-audit display.
- Shared deletion queue/privacy, owner-only proposals/Undo, unanimous current recent-viewer
  consent/aging, non-shortening delays, Arr target pinning, execution claims, files-deleted
  checkpoint and monitoring-only cleanup. Automatic execution is off by default.
- Lidarr/Readarr configuration, selected album/book-edition plans, pinned roots/profiles,
  request limits/receipts, local watches and live path/key-free service/storage projections.
- Separate messaging-operator authorization, rich private inbox metadata/edit/delete events,
  bounded uploads/downloads, immutable announcement previews, normal chat nonces, per-target
  claims/receipts and retention. Jellyfin administration alone does not grant this access.
- Adversarial JSON/body/network budgets, signed settings/review-only earlier plugin configuration,
  independently approved Arr destinations and authenticated consent/message/control records.
  See [SECURITY_REVIEW.md](SECURITY_REVIEW.md); remaining state trust/rollback gaps are not certified safe.
- Optional bundled Activity adapter: short-lived memory-only Discord OAuth sessions,
  audience/scopes/live allowed-guild verification, linked native-token checks, shared media and
  messenger services, SDK account-switch teardown and a host-derived color-only preference bridge.
  It never receives a Jellyfin credential or pretends to be a native player.
- Explicit legacy native-watch import, visible multi-edition alias mirroring/removal and revision
  conflicts; owner/Seerr/configuration/expiry-bound reviewed adoption of remote membership without
  retrying an uncertain write. Background sync does not import old native watches automatically.
- Paginated Discord poster galleries, watchlists, removal/consent queues, updates and book editions;
  selected TV seasons and standard/4K previews; native edition selection and read-only receipt refresh.
- Native/Activity explicit-load posters through a fixed-origin, credential-free, byte/dimension-bounded
  PNG/JPEG fetcher. No client-selected remote URL, active image format, query credential or CSP widening.
- Current-user real native player destinations, explicit command previews, authenticated schema-12
  once-only command receipts, cancellation of unsent previews, manual uncertainty acknowledgement and
  current-player observations that do not claim command-caused playback. Other users' players remain
  unavailable even to administrators; Activity cannot invoke these routes. Companion party control
  cannot be bypassed through native destination playback when the provider reports either party.
- Existing own Seerr request notification polling (one rotating page per scan), pinned own music/book
  request statuses, personal opt-out, current-owner/access projection and user/operator delivery diagnostics.
  Optional HTTPS HMAC wake-ups accelerate **verified notification reads only**; polling stays enabled.
- Owner-only Private SyncPlay policy/account permissions, kick/unblock and shared guest-link revocation
  using fresh reviewed snapshots; explicit Discord/Activity handoff to native controls without party
  bearer disclosure or player impersonation. SDK/provider policy remains authoritative.
- Messaging conversation/order filters, mention navigation and reauthorized retained-reply lookup;
  native detail-page subscription/proposal controls on the supported Jellyfin Web markup.
- Native top-bar bookmark/watchlist and unread notification bell, focused personal pages, separate
  Add/Subscribe actions, removal-proposal state/deadline markers and explicit Yes/No consent.
- Schema-13 complete-set seals and private per-scope generations protect activity omissions,
  watchlist authorizers and consumed request states; retained-state review/quarantine fails closed.
- Discord owned-request removal proposals, reviewed watch-sync recovery, independent notification/
  delay preferences, quotas, complete player pagination/history/acknowledgement, rich retained inbox
  navigation and explicit attachment copies. See [REMEDIATION.md](REMEDIATION.md) for exact limits.
- Bundled release-version tests, SHA-pinned workflow actions, dependency-advisory checks and an
  exact-ZIP install/restart/native-browser/uninstall/reinstall release gate.

## Acceptance gates and intentional boundaries

The implemented paths above have targeted regression coverage; the historical r4 review and later
remediation evidence remain separate in [PROJECT_REVIEW.md](PROJECT_REVIEW.md) and
[REMEDIATION.md](REMEDIATION.md).
This does **not** establish production readiness or exhaustive parity: `FeatureParityComplete` stays
false until all safety/provider/client acceptance gates pass. These bounded implementations must not
be described as universal client support or unlimited service/history coverage.

- [ ] Real Discord/native browser acceptance of OAuth/login resume, forwarded-link, replay,
      wrong-account, cookie/base-path and crawler behavior. Automated flow/controller tests exist.
- [ ] Complete real-media/multi-player acceptance of the typed Private SyncPlay 1.1 native integration.
      Creation/invitation/revoke/leave/end, owner controls and credential-free native handoff are coded.
      [WATCHPARTY.md](WATCHPARTY.md) describes bounds and retained state.
- [x] Two actual Edge clients with the unmodified provider: loopback native admission, decoded group
      video, administrator owner isolation, no native-player party bypass, permission/stale-review
      checks, kick/block/unblock/revocation/fresh-admission/end. Public HTTPS/proxy and wider races remain.
- [ ] Real Lidarr/Readarr/Seerr acceptance of music/book edition identifiers, permissions,
      exclusions, destination defaults/limits and storage/health discovery; broader frontend coverage.
- [ ] Real native/Discord player command delivery, permission/disconnect/cancellation races, series
      episode selection and multiple devices. A command acceptance or current-item observation is not
      proof that Media Hub caused playback. Native player progress comes from the actual player only.
- [x] Generated-media native detail controls, decoded video, two real Edge sessions, same-account
      remote targeting, administrator isolation, unsent cancellation and live permission-loss rejection.
- [ ] Live multi-edition/restricted-library sync, partial snapshots and concurrent external changes.
      Reviewed recovery adopts observed membership locally; it never rolls back upstream requests/media.
- [ ] Own-request/subscription notification coverage with real Seerr/Arr and the configured signing
      producer. Generic static/unsigned Seerr/Arr webhooks do not implement Media Hub's HMAC protocol;
      use polling instead, without adding a sidecar. Notifications contain only user-facing stages.
- [ ] Live Discord messenger acceptance of mention/conversation/retained-reply navigation;
      actual permission loss, rate limits, attachment changes and uncertain-send recovery.
- [ ] Live optional Activity SDK/proxy acceptance of artwork and native-party handoff.
      OAuth/shared adapters/color bridge are coded; no separate listener, credential proxy,
      native-credential iframe or independent theme selector is used.
- [x] Root and `/mediahub` Jellyfin 12.2 / Edge automatic File Transformation injection,
      ordinary-viewer Plugin Pages navigation and authenticated deep-link reloads, with both upstream
      3.0.2.0 engines installed. Scoped automated WCAG A/AA checks and modal keyboard wrapping pass.
- [x] Generated-media restricted-library projection/direct-item rejection, passive user-data
      preservation, watch hiding/restoration and test media-file retention on the owned fixture.
- [ ] Third-party skins, other base paths/deep links,
      full keyboard/screen-reader use and other clients. Unsupported clients keep native behavior.

### Explicit limits, not hidden feature claims

- Seerr owns request permissions/profiles/routing and existing upstream request management; Media Hub
  cancels only unsent previews. Administrator service-profile dropdowns do not override Seerr routing.
- Movie/TV watchlists use Seerr membership; music/books remain local. Alias cleanup covers currently
  visible saved native movie/series identities. Inaccessible/unverifiable records are not reset.
- Own Seerr requests are scanned page-by-page within service/worker budgets; local own music/book
  notifications cover the most recent 200 requests. Feeds retain 250 events/30 days and expose at most
  100 entries, including at most 20 freshly reauthorized own-request entries per read.
- Native play targets are actual same-account capable sessions and individually playable media.
  Series/album/folder navigation stays in Jellyfin. A 5,000-receipt journal, 500 per-user receipts and pending bounds
  fail closed rather than deleting old receipts. Full native remote-play/party concurrency needs acceptance.
- Forwardable provider invitations are not audience-bound Discord invitations. Discord/Activity open
  native controls without sending those secrets; richer recipient-bound admission requires an upstream
  provider contract, not a guessed transport or a revived legacy ticket flow.

## Server acceptance

Local allowlisted ZIP install, real restart, native uninstall/restart and retained-state
reinstall now pass on isolated official Jellyfin **12.2.0**. Anonymous/elevated/operator access
checks and per-user preferences pass; the sampled native fixture is unchanged. This is not
catalog installation or provider/playback acceptance. See [ACCEPTANCE.md](ACCEPTANCE.md).

### Still outstanding

Install/upgrade/restart/uninstall via the downloaded catalog ZIP on latest stable Jellyfin only;
other Plugin Pages/File Transformation builds/base paths; real Arr request/deletion/unmonitor endpoints; native playback
concurrency; actual library restrictions/editions; companion watch-party integration; Discord rate limits,
revocation/audiences; recovery at all external-write boundaries. Verify host files/state and
unrelated Arr profiles/paths/tags/media are unchanged. Use only one plugin instance per Discord token.

No release, push or production deployment/activation has been performed. Only the disposable
localhost test server was created and exercised.
