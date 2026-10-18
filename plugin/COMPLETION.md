# Completion handoff

The implementation is not a production sign-off. `FeatureParityComplete` and automatic removal
remain off. [ACCEPTANCE.md](ACCEPTANCE.md) identifies the tested immutable ZIP and evidence;
historical artifacts and failed probes are retained, not overwritten.

## Active split and deletion-first work

The r13 acceptance below is historical monolith evidence, not acceptance of the
new two-plugin build. The new public API/transport split retains the core GUID
and complete original state directory. The Discord plugin **keeps Private
SyncPlay SDK 1.1.0** and generates provider invitation links through it; there is
no replacement admission or redemption implementation.

Independent core/adapter projects, package allowlists, tests and adapter CI now
exist. Native background queue/watchlist maintenance no longer requires Discord
linking. The initial [administrator workspace](ADMIN_MONITORING.md) browses
backlog, retained terminal proposals and private evidence; it explicitly does
not claim a historical transition journal for old records.

Implemented in newer source: administrator-configurable denied/own-request/any-item
scopes, native movie/whole-series authority across approved Arr destinations, native
permission editing, Discord permission/status operations, item-specific notification
settings/menu extension and an authenticated transition journal. These have local
tests, not complete split runtime acceptance. Native-item cleanup still fails closed
if the native identity cannot be freshly verified after file deletion.

The independent settings/Activity split is now implemented in source: the adapter
owns encrypted file-only configuration, native administrator setup, Activity routes,
assets, OAuth transport and memory-only Activity sessions. Native link approval and
durable account/message/history state intentionally remain under core custody.
Still required: exact-ZIP migration/lifecycle, real service/menu/theme/client and
embedded-SDK acceptance. Broader notification/status/backlog presentation remains
subject to that acceptance matrix, not an inferred pass from unit tests.
Neither `FeatureParityComplete` nor release readiness has been enabled.

The [deletion experience increment](DELETION_EXPERIENCE.md) adds user-scoped status
and required-action inspection, a focused notification-preferences page and optional
personal deletion-notice text. The 338 core / 34 adapter / 102 browser result was
an earlier source checkpoint, not acceptance of subsequent changes. All of these changes are newer than the r2 ZIPs. The earlier
split runtime harness stalled without evidence and was stopped; it is not counted
as acceptance. Current Docker fixture cleanup/availability requires verification
before resuming exact-ZIP tests; do not reset or replace retained state to proceed.

### 2026-10-19 safety and origin acceptance increment

- The earlier source checkpoint passed **364 core / 35 adapter / 109 browser** tests.
  New local ZIPs and explicit evidence limits are retained under
  `artifacts/split-20261019-r1/EVIDENCE.md`; no exact-ZIP runtime acceptance is claimed.
- ZIP SHA-256: core `7292e44bc199ae41a386dd41d6eec54690fd2874de3292fe0c66e867cee98390`;
  adapter `0d6a349f8edf6cdd672d1d5e782797b4506121d567845a7ab450395be54420b6`.
- Schema **14** adds only the deletion journal. Migration preserves format-3 settings,
  private keys, old signed roots and valid old consent seals; it never invents past
  events. Missing/tampered/replayed journals and torn root/SQLite commit pairs block
  the affected authority without auto-repair. Unrelated personal-state seals remain usable.
- Plugin writes check a 256 MiB disk reserve and explicit size budgets. Large bounded
  deletion proofs have a separate encryption budget; general credentials retain their
  smaller existing limits. No host-database or global resource configuration is changed.
- Six real loopback HTTP/TLS/MVC tests pass for root and base-path linking, secure
  cookie scoping, same-browser proof, callback replay, origin allowlists, ignored
  untrusted forwarded headers and an explicitly trusted loopback proxy. Test certificates
  are per-client pinned, never installed as trusted certificates. OAuth/native identities
  in this harness are synthetic; this is not live Discord consent or full Jellyfin acceptance.
- Live `Test-DiscordTransport.ps1` verified the root `.env` bot/application credentials,
  the explicitly designated guild/channel/human member, a client-credentials grant and
  revocation, two test message sends and one generated attachment. Both probe messages
  were deleted using only their returned IDs. No commands, other channels or native
  settings were changed; no credentials were logged. This is a transport probe, not
  end-to-end plugin/Activity/user-OAuth acceptance.
- Docker summary queries respond, but retained `mediahub-test-split` inspection/start
  and fresh `mediahub-test-safety2` startup did not complete. The fresh fixture is
  labelled and remains **Created** with its labelled config/cache volumes and network;
  it has no configured native baseline or private credentials yet. The partial
  `mediahub-test-safety-network` is also retained. No daemon reset, unrelated-container
  change or state deletion was attempted. Test calls now have bounded Docker clients
  and label-scoped ownership checks; the harness array-forwarding issue was corrected.

### 2026-10-19 configuration and Activity split increment

- Latest full run passes **352 core / 64 adapter / 112 browser** tests. Thirteen existing
  Activity/session/loopback tests moved to the adapter suite, not removed. Additional
  tests cover independent encrypted configuration, explicit migration, API-key/admin
  guards, corruption/missing-state isolation, custody after restart, preserved legacy
  bytes, configuration-revision invalidation and migration lock ordering.
- The core has no Activity HTTP controller/session type or embedded Activity bundle.
  The adapter embeds those assets and uses the versioned API, never core types or
  SQLite. Its CI now builds the pinned Activity before testing/packaging; standalone
  core builds no longer require an Activity build. No workflow was pushed or run remotely.
- Reviewed migration adds a nullable authenticated root custody field (omitted for
  existing roots until migration) and new adapter encrypted settings/key files.
  Existing core keys/settings/seals and durable records are not rewritten as fresh.
  A missing adapter directory afterwards cannot trigger legacy fallback or key
  regeneration; unknown/mismatched custody fails closed. Restore both directories
  from matching stopped backups. New roots may be rejected by older binaries;
  downgrades are not accomplished by stripping authentication fields.
- Runtime adapter configuration is exposed only as a bounded trusted in-process
  snapshot. Core-native watchlists remain usable when it is unavailable; Discord
  authorization/delivery is disabled instead of reviving legacy credentials. Runtime
  revisions include adapter edits so pending message plans cannot retain old authority.
- Migration releases the adapter snapshot lock before waiting for the serialized
  core writer. Tests hold a core write transaction and prove snapshot reads still
  finish; a migration must not deadlock a native request.
- Activity revalidates its live session before returning API results, and failed
  authentication grants receive bounded best-effort revocation without retries.
  The public SDK also rejects cancelled queued work before provider dispatch;
  truly in-flight uncooperative writes keep their admission slot until completion.
- A separately verified start of retained `mediahub-test-settings` also timed out.
  Subsequent exact-label summary confirms it remains **Exited**, not a healthy test
  host. No reset/restart of the daemon or unrelated resource change was attempted.
- Plugin warning sinks are failure-contained too: a logging backend exception must
  not escape startup/cleanup or the Discord worker's error handlers. Tests inject
  throwing loggers; host exception policies and logging configuration are unchanged.
- Native OAuth proof pins keep their legacy serialization, and use stable keyed
  secret identity for migrated adapter configuration so restart-only secret
  rewrapping does not invalidate proof. A real secret/configuration change still
  requires re-verification; stale status offers that action instead of an unusable
  completion loop.
- Latest immutable ZIPs and unpassed gates: `artifacts/split-20261019-r2/EVIDENCE.md`.
  Core SHA-256 `ac516ba1a35a40b596d563a7e197598b59befe9c63ed9922788fb0aa827fa767`;
  adapter SHA-256 `352dc0d921ebfc42b4ddaed8f9f283d112a64cf737bcf896abd4adced1bf4114`.

## Local gap closure

- Non-root Plugin Pages links resolve the server base path **once**, including bookmark,
  notification and login-handoff links. `Test-Completion.ps1` uses a distinct owned Jellyfin
  fixture with `/mediahub`, automatic dependency injection and authenticated deep-link reloads.
- Notification/page refresh keeps focus on the same item's action, not another identically named
  Yes/No. Expanded Help persists only for currently visible notices; identity/access teardown clears it.
  Consent groups identify their title and Help controls reference their detail regions.
- Modal Tab/Shift+Tab wrapping prevents focus falling onto the document behind expanded Help.
  Native browser acceptance exercises keyboard navigation plus scoped WCAG A/AA automated checks
  with test-only **axe-core 4.14.0**. This is not a screen-reader or universal-accessibility certification.
- The release gate runs root and non-root exact-ZIP lifecycle/browser acceptance. The axe/browser
  tooling is build/test-only, never a runtime dependency or a provider bundled into Media Hub.
- Generated-media library/player tools are confined to `mediahub-test-visibility` on loopback
  port 18496. They create only blank test videos and disposable native accounts/libraries.
  Restricted-library and passive-data checks, retained watch hiding/restoration, actual decoded
  playback in one/two Edge sessions, administrator player isolation, cancellation and live
  permission-loss checks pass. An HTTP-accepted command alone is never playback evidence.
  The original pre-library fixture baseline is not overwritten. Provider groups and public
  HTTPS/proxy invitation flows remain separate from these native destination tests.
- The unmodified Private SyncPlay provider also passes two independent, real-client loopback
  scenarios: actual admission/group video, owner boundaries, permission/stale-review controls,
  kick/block/unblock, invitation revocation/fresh admission and end. No provider engine ships
  with Media Hub. Public HTTPS/proxy link opening and a broader concurrency/client matrix remain
  separate; this loopback test does not certify them.
- Security/remediation documentation distinguishes schema-13 authenticated sets from unsealed
  projections and complete stopped-snapshot rollback. Old r8 counts are not current status.

## Gates requiring external access or human acceptance

| Gate | Required input / decision | Completion condition |
| --- | --- | --- |
| Discord/OAuth/Activity | Test application credentials and explicit guild/channel/user are now supplied privately; transport-only acceptance passes. A permanent domain is not available or required. Use disposable loopback/proxy harnesses for origin-sensitive cases. | Plugin-level dual-identity/account-switch/permission-loss, delivery/rate-limit and Activity SDK/proxy tests. Human OAuth/embedded SDK behavior must not be inferred from client-credentials or synthetic-grant tests. |
| Seerr/Arr/music/books | User authorizes owned Docker fixtures and generated test media only. Docker startup must become usable without resetting unrelated resources. Selected music/book edition fixtures are still needed. | Actual request/notification/reconciliation and file-ID-only removal/cleanup, including ambiguity and concurrent activity/ownership changes. |
| Wider Private SyncPlay/client acceptance | Defined client/fault matrix with disposable loopback/TLS/proxy fixtures. Historical two-client loopback admission/playback/moderation passes, not for the split build. | Wider capacity/disconnect/account/library-permission-loss/concurrency checks. Public DNS/certificate/proxy deployment is an operator check, not a required permanent test origin or a claimed live pass. Recipient-bound bearer links require upstream support, not a local bypass. |
| Published catalog / GitHub runner | Explicit authorization to commit/push/run the workflow and publish an immutable test release in the intended repository. None is inferred from local tests. | Real runner gate/provenance and installation of the downloaded catalog artifact, preserving catalog history and repository identity. |
| Themes / assistive technology / other clients | Named supported third-party skins, clients and human screen-reader testers. | Manual usability/contrast/keyboard/accessibility acceptance on that defined matrix. Automated scoped checks do not certify unspecified skins. |

No missing prerequisite is replaced by a fake player, a guessed provider contract, production
activation, a credential proxy, a sidecar, forced state recovery or a skipped release check.
