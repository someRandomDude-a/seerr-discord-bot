# Plugin feature checklist

This tracks the Media Hub plugin's implemented features and remaining work, **not a release or
server-testing readiness claim**. Native pages, Discord commands and the bundled Activity share
the same application services and permission checks. The existing API readiness field
`FeatureParityComplete` stays false until every feature and safety gate is complete.

## Connected, with automated tests

- Plugin-only persistence (schema 11), additive upgrades/snapshots, matching authenticated settings/key,
  failure-contained lifecycle and retained state on uninstall.
- Integrated first-install administrator tutorial with real settings forms, manual save boundaries,
  Back/Next, pause/resume and restart without resetting configuration. Browser-local progress stores
  only tutorial state, not credentials/drafts; Discord permissions, linking and Activity setup are included.
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

## Remaining feature and verification work

- [ ] Real Discord/native browser acceptance of OAuth/login resume, forwarded-link, replay,
      wrong-account, cookie/base-path and crawler behavior. Automated flow/controller tests exist.
- [ ] Resolve exact creator-session SyncPlay membership via supported native services/events.
      Invitations are blocked in its absence. Do not use host-private maps or trust client assertions.
- [ ] Real Lidarr/Readarr/Seerr acceptance of music/book edition identifiers, permissions,
      exclusions, destination defaults/limits and storage/health discovery; broader frontend coverage.
- [ ] Rich native Discord poster galleries, all watchlist pages, advanced season/profile/edition
      controls, device destinations, full status/storage/Seerr navigation and request management.
- [ ] Multi-edition/existing-preview-watch reconciliation, incoming removal across all aliases,
      adversarial concurrent sync tests and explicit reviewed recovery for uncertain operations.
      Current pre-increment local watches need their bell reasserted to create sync intents.
- [ ] Full own-request/subscription notifications, signed hooks with fallback polling, music/book
      status coverage and delivery/operator diagnostics. Do not emit alerts for internal-only stages.
- [ ] Live Discord messenger acceptance, mention/reply navigation and remaining messaging polish;
      actual permission loss, rate limits, attachment changes and uncertain-send recovery.
- [ ] Live optional Activity SDK/proxy acceptance, rich galleries/artwork and native-party handoff
      UI. OAuth/shared adapters/color bridge are coded; no separate listener, credential proxy,
      native-credential iframe or independent theme selector is used.
- [ ] Native detail-page controls, navigation polish, administrator queue/audit diagnostics,
      comprehensive base-path/theme/accessibility and stale-view tests.

## Server acceptance

Local allowlisted ZIP install, real restart, native uninstall/restart and retained-state
reinstall now pass on isolated official Jellyfin **12.2.0**. Anonymous/elevated/operator access
checks and per-user preferences pass; the sampled native fixture is unchanged. This is not
catalog installation or provider/playback acceptance. See [ACCEPTANCE.md](ACCEPTANCE.md).

### Still outstanding

Install/upgrade/restart/uninstall via the downloaded catalog ZIP; baseline and newer Jellyfin;
Plugin Pages/File Transformation; real Arr request/deletion/unmonitor endpoints; native playback
concurrency; actual library restrictions/editions; multi-browser SyncPlay; Discord rate limits,
revocation/audiences; recovery at all external-write boundaries. Verify host files/state and
unrelated Arr profiles/paths/tags/media are unchanged. Use only one plugin instance per Discord token.

No release, push or production deployment/activation has been performed. Only the disposable
localhost test server was created and exercised.
