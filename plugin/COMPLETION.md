# Completion handoff

The implementation is not a production sign-off. `FeatureParityComplete` and automatic removal
remain off. [ACCEPTANCE.md](ACCEPTANCE.md) identifies the tested immutable ZIP and evidence;
historical artifacts and failed probes are retained, not overwritten.

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
| Discord/OAuth/Activity | Dedicated **test** Discord app/guild/users, configured redirect/proxy mappings and explicit permission to send test messages/files. Supply secrets through a private local file or secret store, not chat/log output. | Live dual-identity, crawler/forwarded/replay/account-switch, permission-loss, delivery/rate-limit and Activity SDK/proxy tests. |
| Seerr/Arr/music/books | Dedicated disposable service instances, test accounts, selected edition identifiers and independently approved test destinations. No production credentials or media. | Actual request/notification/reconciliation and file-ID-only removal/cleanup, including ambiguity and concurrent activity/ownership changes. |
| Wider Private SyncPlay/client acceptance | An agreed client/fault matrix and a deployed test public HTTPS/proxy origin. Two-client loopback admission/playback/moderation now passes. | Public-link opening, wider capacity/disconnect/account/library-permission-loss/concurrency tests. Recipient-bound bearer links require upstream support, not a local bypass. |
| Published catalog / GitHub runner | Explicit authorization to commit/push/run the workflow and publish an immutable test release in the intended repository. None is inferred from local tests. | Real runner gate/provenance and installation of the downloaded catalog artifact, preserving catalog history and repository identity. |
| Themes / assistive technology / other clients | Named supported third-party skins, clients and human screen-reader testers. | Manual usability/contrast/keyboard/accessibility acceptance on that defined matrix. Automated scoped checks do not certify unspecified skins. |

No missing prerequisite is replaced by a fake player, a guessed provider contract, production
activation, a credential proxy, a sidecar, forced state recovery or a skipped release check.
