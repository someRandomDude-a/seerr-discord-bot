# Isolated Jellyfin acceptance · updated 18 October 2026

This is limited smoke-test evidence, **not feature-completion or production certification**.

## Current local gap-closure package · r13 · 18 October

- Selected immutable artifact:
  `plugin/artifacts/features-20261018-r13/repository/Jellyfin.Plugin.MediaHub_0.1.0.0.zip`.
  Jellyfin **12.2.0**, plugin `0.1.0.0`, schema **13**. r11/r12 remain historical artifacts.
- ZIP SHA-256: `5441050e7aac1b774c20f7c095d5bb5ae66882dce2ef499e16c6514c926ba0b3`.
  MD5: `b162124989892d02445ed49f93d86511`; **1,905,383 bytes**.
  Package/installed DLL SHA-256: `2d8e1e766adc947f998a1c5d53e341ef2346adb3649eef8ac160583dc81b3e13`.
  Copies from both stopped root/non-root fixtures match the package build independently of install checks.
- **338 native / 95 browser-repository regressions** pass. Activity and zero-warning package builds,
  harness syntax, actionlint and tracked whitespace checks pass. Plugin direct/transitive NuGet and
  Activity production npm advisory checks report no known vulnerabilities; this is not certification.
- **Root and `/mediahub` exact-ZIP lifecycle/browser acceptance pass** with the separately installed
  reviewed File Transformation / Plugin Pages 3.0.2.0 builds. The non-root fixture is the distinct
  `mediahub-test-completion` at loopback port **18396**. Both sampled native baselines and retained
  plugin state survive uninstall/reinstall. Both fixtures are **stopped**, with Discord/removal off.
- Fixed the actual non-root failure: Plugin Pages prefixes `pageUrl` via ApiClient, so nested resource
  links must not already include the server base path. Bookmark, notification and native link-handoff
  URLs now follow that contract. Authenticated notification deep-link **reloads** pass without manual
  plugin script loading or duplicate navigation. Signed-in ordinary users can access both personal pages.
- Keyboard tests now cover Yes → No → Help, Enter-to-expand and Tab/Shift+Tab modal wrapping,
  including long expanded rules. Item-specific focus and expanded Help survive refresh and disappear
  on current visibility/account loss. No implicit settings or viewer POSTs occur in browser smoke.
- Test-only **axe-core 4.14.0** reports **zero scoped WCAG A/AA violations** on native settings,
  notifications, compact consent and expanded consent at the non-root fixture. This is an automated,
  scoped check, not human screen-reader, arbitrary-theme or all-client acceptance. Consent samples
  remain explicitly labelled mock data with mutations blocked; they are not real deletion votes.
- Release smoke now requires axe and the independent non-root exact-ZIP gate before artifact
  publication. It passes local lint/testing; a real GitHub runner/public catalog remains unverified.
- **Generated-media native acceptance passes** on the separately labelled `mediahub-test-visibility`
  fixture, loopback port **18496**, using the r13 ZIP and only three generated blank test movies.
  Test setup deliberately creates two libraries and restricts its disposable viewer through native
  APIs, retaining the original empty baseline. Hidden media is absent from viewer catalog results,
  direct hidden controls are rejected, and passive reads preserve native playback user data.
  A saved watch is hidden after library access loss and reappears after explicit access restoration;
  the inaccessible retained record is not erased. Test media-file hashes remain unchanged.
- The real authenticated Edge client loads the native detail controls and decodes advancing generated
  video following one explicitly confirmed command. A separate **two-browser test** verifies exact
  same-account remote playback, rejects administrator access to another account's players, confirms
  cancelled previews do not play, and rejects confirmation after live playback-permission revocation.
  The disposable native permission is restored. HTTP acceptance alone is not counted as playback.
  These are actual native-client tests, not mocked players or fabricated session capabilities.
  They do not establish every disconnect race or other-client behavior.
- **Real provider/two-client acceptance passes twice in independent restart scenarios** on the
  generated-media fixture with the unmodified, separately installed **Private SyncPlay 0.1.5 / SDK
  1.1.0**. Its release ZIP SHA-256 is
  `d467ba8f4122dcb015f80afac5c18bad02037200fcf727c3bff88d8ea0d2ea8a`; the provider's API DLL matches
  the reviewed bundled SDK. Native provider admission produces two verified memberships and decoded,
  advancing group video in both actual Edge clients. A signed-in administrator participant cannot
  use Media Hub's owner-only invite/end operations; ordinary destination playback cannot bypass a
  companion party. Fresh owner permission updates, stale-review rejection, kick/block, explicit
  unblock without automatic rejoin, revoked-link rejection, fresh admission and owner end all pass.
  No guest admission is requested/enabled by the test, and no native credential or bearer is logged.
- Provider admission is deliberately tested on the known **loopback origin** using only the returned
  invitation's fragment; the configured public HTTPS address remains non-routable test data.
  This is **not** public HTTPS/proxy/Discord invitation-link acceptance and does not certify all
  capacity, disconnect, activity/access-loss or mixed-client races. An earlier broadly labelled
  moderation probe failed and is retained; it is not counted as a pass. Subsequent granular scenarios
  pass independently without retrying an uncertain Media Hub mutation.
- The release gate now also runs generated-library, single/two-player and provider-client acceptance
  using a checksum-pinned **test-only** provider download outside the release artifact. These gate
  changes still require a real GitHub runner. All provider-engine files remain excluded from Media Hub.
  Final label-guarded verification confirms all three owned fixtures are **stopped**, their test
  volumes/state are retained, and the selected r13 ZIP has not changed. Production is untouched.
- See [COMPLETION.md](COMPLETION.md) for the remaining live-service, real-client/concurrency,
  specified third-party-theme/human-accessibility and release prerequisites. `FeatureParityComplete`
  remains false. No commit, push, publication or production activation was performed.

## Historical native Web integration package · r11 · 18 October

- Selected immutable artifact:
  `plugin/artifacts/features-20261018-r11/repository/Jellyfin.Plugin.MediaHub_0.1.0.0.zip`.
  Target **Jellyfin 12.2.0 / .NET 10**, plugin `0.1.0.0`, schema **13**. Earlier artifacts are retained.
- ZIP SHA-256: `02992733d33fd282513596f18197c249666edb869c0aac0eda99fe7d3f7206e2`.
  Catalog MD5: `1e500e5fee796584926936ed9d01471e`; **1,904,607 bytes**, ten allowlisted entries.
  ZIP/current-build/package-build/installed DLL SHA-256:
  `4db9119589ed106a78a5719c990bb994feadbd85388effd5518ad474d29e5c5e`.
  The install harness verifies the installed DLL on each install; a final copy from the stopped
  fixture independently matches this digest without restarting it.
- **338 native tests** and **92 browser/repository tests** pass; Activity build and zero-warning
  package build pass. Local actionlint, PowerShell/JavaScript harness syntax and whitespace checks pass.
- Separately installed, unmodified upstream **File Transformation 3.0.2.0** and **Plugin Pages
  3.0.2.0**, using their Jellyfin-12.2.0 archives and verified release SHA-256 hashes in
  `repository/web-dependencies.json`, are active in the label-verified owned fixture only. Neither
  engine is bundled in the Media Hub ZIP. Runtime compatibility uses upstream minimum/API checks,
  not exact version equality; see [WEB_DEPENDENCIES.md](WEB_DEPENDENCIES.md).
- **Exact-ZIP lifecycle passed**: install/restart, native uninstall/restart/reinstall and final state,
  personal-isolation, access and six malformed-body checks. Sampled native users/library/configuration
  and retained plugin state remain intact. Discord and automatic removal remain disabled. The
  owned `mediahub-test-settings` fixture at `127.0.0.1:18296` is **stopped**.
- Stock Jellyfin 12.2 / Edge browser smoke now uses **automatic upstream injection only**—no manual
  script loading. Settings labels, Help/drafts, navigation teardown, keyboard focus, 390px layout,
  dialog contrast, Escape/focus restoration and a single visible bell after header replacement pass.
  A separate ordinary-viewer session opens watchlist and notifications through the real Plugin Pages
  route; both dependency registrations report ready. **Zero implicit settings or viewer POSTs**.
- Consent presentation is **Allow deletion? / Yes / No / Help**, using native primary/neutral button
  classes and collapsed rules, without a replacement color palette. The native visual sample uses
  explicitly labelled **mock consent data**, with every mutation blocked. Help opens without voting;
  no real media, consent, deletion, Seerr/Arr, Discord or player operation is certified by that sample.
  Screenshots under the approved test directory are named `mediahub-native-features-20261018-r11`
  plus `.png`, `-mobile.png`, `-notifications-mobile.png`, `-watchlist-mobile.png`,
  `-consent-preview.png` and `-consent-help-preview.png`.
- r9/r10 are retained failed integration iterations, not accepted substitutes. Testing exposed the
  provider's exact-key-over-regex precedence and Jellyfin's bubbled DIV `pagehide` during login.
  The shared exact `index.html` pipeline, load-context-safe JSON registration and window-transition
  teardown fixes are covered by regressions and this live root-path smoke.
- Remaining gates: published-catalog and real GitHub-runner execution; supported third-party themes,
  screen readers, non-root base paths/deep links and other clients; live services/Discord/OAuth,
  restricted libraries and real playback/provider concurrency. `FeatureParityComplete` stays false.
  This does not certify all upstream plugin behavior or upgrade paths, nor repeat a comprehensive
  security/advisory audit of their independently installed binaries.

## Historical remediation package · r8 · 18 October

- Selected immutable artifact:
  `plugin/artifacts/features-20261018-r8/repository/Jellyfin.Plugin.MediaHub_0.1.0.0.zip`.
  Earlier package sections below are historical evidence, not substitutes for testing these bytes.
- Current target: Jellyfin **12.2.0 / .NET 10**, plugin version `0.1.0.0`, schema **13**.
- ZIP SHA-256: `76b8af03b113ebf5635e86218839a8efca42e822b8b8c5e6af3c20f311ae2cd3`.
  Catalog MD5: `5208dfa09e276d64694e75295672decd`; **1,899,393 bytes**, ten allowlisted entries.
- ZIP/current-build/package-build DLL SHA-256:
  `3cf6d086c917901a1aa94cbcd0f272d49a89acccbeb18ff996cabb0dec09f4cb`.
- **334 native tests** pass against the bundled release-version assembly, after Activity build;
  **89 browser/repository tests** pass. Package build has zero warnings/errors. Workflow actionlint,
  whitespace validation and known direct/transitive NuGet / Activity production npm advisory checks
  pass. Advisory checks are not a comprehensive security certification.
- New adversarial regressions cover deleted local-only activity after native timestamp failure,
  forged/omitted watchlist authorizers, music/book consumed-state reset after simulated upstream
  removal, old valid SQLite seal replay, missing seals/root, empty retained unsigned databases and
  preference/request-only maintenance selection. No signing key or production integration was used.
- See [REMEDIATION.md](REMEDIATION.md) for R1–R7 implementation details, private-root backup/recovery
  requirements, the Discord capability matrix and remaining live-service/companion acceptance.
- The r5–r7 iterations are retained, not overwritten. Screenshot checks found and corrected browser
  dialog defaults and a duplicate hidden legacy bell during toolbar replacement. R8 removes stale
  plugin navigation when the active header changes; a regression covers the temporarily empty
  replacement toolbar. Earlier failed UI probes are not counted as acceptance of this package.
- **R8 exact-ZIP lifecycle passed** on the label-verified `mediahub-test-settings` fixture at
  `127.0.0.1:18296`: install, restart, native uninstall/restart, reinstall and final access/state check.
  Native sampled users/library/configuration remain unchanged; personal isolation and all six
  malformed-body rejections pass. Settings/key/private-root checkpoints and plugin state are retained.
  Discord and automatic removal remain disabled. The owned fixture is **stopped**.
- Stock Jellyfin 12.2 / Edge acceptance passes desktop settings, generated labels, section/Help draft
  preservation, keyboard focus, 390px layout, private-draft teardown, **one visible working top-bar
  bell after header/page replacement**, the focused watchlist page and the themed notification modal.
  Dialog foreground/background contrast is checked with alpha compositing against the stock surface;
  Escape returns focus to the bell. Browser navigation/Help produce **zero implicit settings POSTs**.
  Installed DLL SHA-256 matches the ZIP/current/package-build digest above after reinstallation.
- Native screenshots under the approved external test directory:
  `mediahub-native-features-20261018-r8.png`, `-mobile.png`, `-notifications-mobile.png` and
  `-watchlist-mobile.png`. These test resources are loaded from the installed assembly, explicitly
  bootstrapped because the stock fixture does not install the optional Web integration engines.
  Separate local-source mock consent screenshots exercise Yes/No presentation only; they are not
  evidence of real media, votes, Discord delivery or provider operations.

## Historical audited package and host · 16 October

- Official Jellyfin **12.2.0**, pinned image:
  `jellyfin/jellyfin@sha256:357724bf0ae27a672c7cbaa899db2d9abeb13dbd8657ccce750258a4c059d037`.
- Disposable Docker Desktop container `mediahub-test-brave-otter`, published only at
  `http://127.0.0.1:18096`. Dedicated labelled config/cache volumes and test networks;
  no existing media mounts, production credentials, container/network reassignment or VPN/DNS edits.
- Local preview `0.1.0.0`, compiled against the **12.0.0 API baseline**, installed as the
  allowlisted ZIP, not test build output:
  `plugin/artifacts/acceptance-12.2-20261016-audit-r1/repository/Jellyfin.Plugin.MediaHub_0.1.0.0.zip`.
- Verified catalog MD5 and independent ZIP SHA-256:
  `09a8bd3292360dc81622d42ebfa65bd8b06ae55be022c05b9b11e04d5eeea27b`.
  Catalog MD5: `77a135a2a0ec1d338454b9d64d1d9a54`; archive size: 1,782,492 bytes.
  The ZIP has seven allowlisted runtime files plus two license notices; no host assemblies,
  secrets, tests or developer-machine native binaries.

## Passed on the running server

- Host reports 12.2.0 and exactly one **Active** Media Hub plugin; the host plugin-list
  endpoint and embedded configuration HTML/JavaScript remain usable.
- Anonymous private API access is rejected. An ordinary viewer cannot access elevated
  settings or operator messaging. Disabled Activity cannot authenticate/read private data.
- Personal preferences remain isolated between administrator and viewer accounts.
- Real host restart retains personal revisions/preferences and plugin settings. A non-routable
  `https://mediahub.invalid` value exercises only the plugin's own settings file; no provider
  or public application is configured by this fixture.
- Upgrade of the retained secret-free fixture blocks ordinary plugin reads while its format-2
  settings await review. The elevated explicit save produces format 3; the previous format-2
  file is retained. No provider address, credential, Discord or deletion feature is activated.
- Jellyfin's native plugin-uninstall API followed by a host restart removes the plugin routes
  but retains its database/key/settings. Reinstalling the same ZIP retains the recorded
  personal revision/preferences; stable key/settings hashes match across uninstall.
- Recorded native account IDs/names/admin/disabled flags, virtual-folder projections and
  sampled server configuration fields match the pre-install fixture across these operations.
- Discord gateway, Activity and automatic deletion stay disabled. No real media, request,
  message, player or deletion operation is exercised.
- Six authenticated malformed JSON bodies (duplicate/case-alias fields, truncation, invalid
  typed binding, null preferences, excessive depth and oversized input) are rejected with
  400/413. The marker in the invalid binding is not reflected. Personal state and host health
  remain unchanged after rejection.
- The installed plugin assembly's SHA-256 matches the packaged build after the audited ZIP's
  real restart/native-uninstall/reinstall cycle. Both native fixture and retained plugin
  preferences/settings checks pass throughout that cycle.

The harness creates disposable accounts through supported Jellyfin setup APIs before taking
its native snapshot. Normal native session/log/plugin bookkeeping is expected; this is not a
claim that Jellyfin's database files are byte-identical during normal server operation.

## Recorded audited-package automated checks

- **248 native tests** pass against each of Jellyfin API **12.0.0** and **12.2.0**,
  including adversarial settings/JSON, altered consent/deadlines, message/attachment integrity,
  OAuth proof fields, control replay and schema-10-to-11 review-only deletion upgrades.
- **39 browser tests** pass, including unsigned-configuration review, OAuth confirmation/stale-account handling, Activity
  proxy-root routing/account teardown, native authorization headers and credential-free
  Activity attachment transport.
- The Activity bundle builds with pinned dependencies and embedded upstream license notices;
  the baseline package build completes with no warnings or errors.

## Current plugin-only source verification

The repository now contains only the plugin, its documentation, tests and release/acceptance
tooling. Current checks pass **334 native tests on latest stable Jellyfin API 12.2.0** and **89 browser/repository
tests**, including the conventional settings/draft lifecycle, plugin-only source/workflow policy and
local documentation links. The bundled Activity builds and both active GitHub workflows pass
`actionlint` validation.

The native UI now follows Jellyfin Web v12.2 label/container/checkbox and list conventions;
structural regression coverage checks the component markup and absence of native-page CSS
overrides. This is not visual acceptance of every skin. See [SKINNING.md](SKINNING.md) for the
separate Discord Activity boundary and remaining skin/client checks.

The native watch-party adapter uses the reviewed Private SyncPlay public SDK 1.1.0 from provider
0.1.5. Checks cover missing/unhealthy providers, exact sessions/ownership, private atomic creation,
signed-in-only one-use links, hostile provider replies/URLs, uncertain writes/cancellation, teardown
and SDK-only packaging. Retired routes/records stay inert. See [WATCHPARTY.md](WATCHPARTY.md).
Cancellation coverage also includes a provider that blocks synchronously before returning its Task;
its single retained slot prevents further calls after the request has timed out. A native watch-party
403 clears only that optional feature instead of hiding otherwise permitted personal media.
Bearer-link clicks, middle clicks and copy/open context menus pass through the native page's
local account boundary too; an account switch clears links rather than navigating a stale secret.

Build, CI and catalog now target **only latest stable Jellyfin 12.2.0**. The older two-version
results and 12.0 baseline packages above/below are preserved historical evidence, not current support.

These source checks do not replace the recorded host lifecycle evidence above. The first-run
settings redesign, UI updates and typed watch-party integration were not installed into that recorded package;
a fresh package's host lifecycle and catalog/provider/client acceptance are separate gates. Retained test
artifacts/checksums and runtime state were not rewritten by source cleanup.

A fresh plugin-only baseline package was built with no warnings/errors at
`plugin/artifacts/plugin-only-20261016-r1/repository/Jellyfin.Plugin.MediaHub_0.1.0.0.zip`.
Its nine-file runtime/license allowlist, catalog ABI/MD5 and independent SHA-256 were checked:
`46e2986a620f910b9f3cdf157165fe9f79aaef70f21144d7250c62d0fb10d4d9`.
It is local-only, not published or installed by this cleanup.

## Private SyncPlay integration smoke test · 17 October 2026

A **new** labelled/isolated fixture, `mediahub-test-private-syncplay` on localhost `18196`, uses
the same pinned official Jellyfin **12.2.0** image without media mounts. The earlier fixture and
its package/state were not replaced. The initial run installed this allowlisted Media Hub ZIP:

- `plugin/artifacts/watchparty-20261017-r1/repository/Jellyfin.Plugin.MediaHub_0.1.0.0.zip`
- ABI **12.2.0**, 10 runtime/license entries, 1,816,212 bytes.
- ZIP SHA-256: `4a23c495ba268eeed5aed9bee0c99607f83f1049fd4c0efadefc4bfb08e268e6`.
- Catalog MD5: `6a10bc95d23180aef93bec257f3c03f2`.

The separately installed provider is the reviewed upstream **PrivateSyncPlay-0.1.5.zip**
(SHA-256 `d467ba8f4122dcb015f80afac5c18bad02037200fcf727c3bff88d8ea0d2ea8a`).
Only its public SDK is included in Media Hub; the actual provider remains in its own plugin folder.

Passed through the real host and SDK load-context adapter:

- Current Media Hub ZIP install/discovery, sampled native fixture preservation, personal isolation,
  six malformed-body rejections, and disabled Discord/automatic deletion.
- Missing provider returns 503 before installation; separately installed provider is active and
  the same Media Hub client then queries its current party successfully.
- Atomic private-party/signed-in invitation creation uses the actual authenticated player despite
  forged `UserId`/`SessionId` body fields. Links use only the administrator-configured HTTPS base.
- A duplicate create is rejected; another native user/player does not inherit the party and cannot
  end it. The Media Hub status projection contains no private join code.
- Explicit invitation generation/revocation, owner end and session leave succeed. A real restart
  invalidates the provider's in-memory party. Sampled native users/library/configuration are unchanged.
- The harness prints no credentials/invitation URLs. Existing production services, credentials,
  networks, VPN/DNS assignments and the earlier acceptance fixture remain untouched.

The **final source-matching r4 package** also passes these checks and real native uninstall/restart/
reinstall with retained settings/key/preferences and the provider still in its separate folder:

- `plugin/artifacts/watchparty-20261017-r4/repository/Jellyfin.Plugin.MediaHub_0.1.0.0.zip`
- ABI **12.2.0**, 10 runtime/license entries, 1,816,549 bytes; zero build warnings/errors.
- ZIP SHA-256: `c06a4a85f448896c8a2329be8945eb3b5593db4d00a7152ef9ab2333fefec152`.
- Catalog MD5: `1a1c314181cafa584368c5c9a73b0fb5` (independently checked).
- Embedded native/watch-party/Activity resources match current source byte-for-byte. The stopped
  fixture's installed assembly hash matches the final package build. The new fixture is stopped;
  its volumes/private credentials and all historical package artifacts remain retained.

Reproducible harness: `plugin/tools/Test-WatchParty.ps1`, after the owned latest-host/package fixture
is created with `Test-Jellyfin.ps1`. This is **not actual media playback**, recipient redemption,
multi-browser concurrency, companion UI, guest/owner moderation or Discord/Activity acceptance.
The ZIP is local-only; no publication, catalog install or production activation occurred.

## Bugs caught by real-host testing

- `BasePlugin` metadata needed explicit initialization; omitting it malfunctioned plugin
  discovery and the host plugin-list endpoint. A metadata-only constructor and regression
  test now cover this without initializing persistence during discovery.
- Jellyfin 12 disables legacy authorization headers by default. Test requests and native file
  transfers use the supported `Authorization: MediaBrowser …` header instead.
- Readiness must check `/health`, not just public info: Jellyfin 12 exposes public info on a
  temporary setup listener before its real application takes over the port.
- Windows PowerShell's REST array wrapping, native executable resolution/quoting and GUID
  formatting required harness fixes. Existing recorded snapshot values were normalized,
  not replaced with a new baseline from the live server.

## Implementation-gap package · 18 October 2026

Current source adds the conventional settings workspace and typed discovery, watchlist alias/import/
reviewed recovery, galleries/seasons/editions, own-request notifications and signed read-only hints,
messenger navigation/diagnostics, owner-only companion moderation and native handoff, recognized Web
detail controls, and actual same-account native player-command previews/receipts. Schema **12** adds
only the authenticated player journal; schema-11-to-12 backup/retention has a targeted regression.
See [FEATURES.md](FEATURES.md), [PLAYBACK.md](PLAYBACK.md) and [NOTIFICATIONS.md](NOTIFICATIONS.md)
for exact bounds and unimplemented provider semantics—not an unlimited parity claim.

- Final local source-matching package:
  `plugin/artifacts/features-20261018-r4/repository/Jellyfin.Plugin.MediaHub_0.1.0.0.zip`.
- SHA-256: `b8b667aa293226fae25e8b356e11c95625af2f8322a83098582bb62b5ac6dd00`.
- Catalog MD5: `40f6cf4d0ac9cba16ecf3bd1053723bc`; ABI `12.2.0`; 1,876,944 bytes;
  ten allowlisted runtime/license entries. Independent ZIP-entry/build hashes and catalog checks pass.
- Final-source and package-build assembly SHA-256 both equal
  `a6b8d347149a0e71fa09355a6c8816f4d9e07ee3ccce90fcc00446751562034f`.
- **317 native tests**, **79 browser/repository tests**, Activity build, both workflow actionlint
  checks and `git diff --check` pass. Package build has zero warnings/errors. New coverage includes
  native play replay/uncertainty/permissions/party boundaries, legacy aliases and concurrent sync,
  authenticated reviewed recovery, existing own-request projection, signed hints, artwork bounds,
  owner moderation, native-only details without Seerr and private/stale UI navigation.

After the user started Docker, the owned settings fixture `mediahub-test-settings`
(`127.0.0.1:18296`) passed installation of this exact schema-12 package, a real Jellyfin restart,
native uninstall/restart and reinstall. Jellyfin reports the plugin active. Each installed check
passed personal isolation, six malformed-body rejections, settings/discovery access checks and
sampled native user/library/configuration comparisons. Stable plugin settings/key digests stayed
unchanged across uninstall; retained personal preferences/revisions survived reinstall. Discord and
automatic removal stayed disabled. The fixture was stopped afterward; volumes and private credentials
were retained. Original acceptance/companion fixtures and production containers were not modified.

Stock Jellyfin Web **12.2 / Edge** settings acceptance now passes on r4: real native login, settings
visibility, Jellyfin-generated field labels, section-switch draft retention, contextual Help,
keyboard focus and a settings selector fitting a **390 × 844** viewport. A second run, without
initializer instrumentation, also verified that native navigation clears private drafts. No implicit settings POST
occurred. Desktop **1280 × 960** and narrow full-page screenshots were inspected; native stock styling
is inherited without a plugin stylesheet. This is limited settings evidence, not full accessibility
or third-party-skin certification.

After the final UI run, the owned fixture was verified stopped and its installed plugin assembly was
copied read-only for hashing: it matches the r4 package/build SHA-256 above. Private test credentials,
plugin state and dedicated volumes remain retained. Screenshots are in the approved temporary folder:
`mediahub-settings-native-20261018-r4.png` and `mediahub-settings-native-20261018-r4-mobile.png`.

That browser run found and fixed a real native-field incompatibility in r3: Jellyfin v0 rejected v1
creation options, and label callbacks need a parent container. R4 uses container-parsed input/select
markup and the host's v0 button/checkbox creation signature; two additional browser regressions cover
that contract. The harness also needed the stock username selector and completion of native home
navigation before opening plugin settings. Real native playback/media requests, companion mutations,
live upstream integration and published-catalog installation remain unperformed. Earlier Docker
daemon/fixture guards made no changes; Docker Desktop was started by the user, not restarted by this agent.

`plugin/tools/Test-NativeUi.mjs` provides a test-only Playwright/Edge check for that specifically owned
fixture, using its private credential file without logging values. It checks native generated labels,
section draft retention, Help behavior, narrow-screen focus, navigation teardown and no implicit settings POST; it is a developer tool, not
another plugin runtime dependency. Run it only after deliberately making Docker available and
installing the new package into the owned fixture. Actual custom-skin/media/client acceptance remains open.

## Reproduce safely

`plugin/tools/Test-Jellyfin.ps1` modes: `Start`, `Configure` (resume owned partial setup),
`Install -Package <ZIP>`, `Check`, `Restart`, `Uninstall`, `Stop`. The script refuses foreign
containers, existing installation targets and unrelated retained setup state. Credentials
remain in the user's approved temporary directory, outside the repository and command output.
`Stop` retains test volumes/credentials; no broad cleanup is performed.

## Still required

Published catalog/download installation; populated older-schema authorization/recovery acceptance
(historical empty-integration fixtures upgraded to schema 11; the new schema-12 package has not yet
been exercised on a running host or populated pending-work fixture);
compatible Plugin Pages/File Transformation; actual restricted libraries/editions and playback
concurrency; live Seerr/Arr requests/deletion/unmonitoring; Discord OAuth/cookies/proxy, operator
messaging and rate limits; companion watch-party integration/multi-player acceptance; remaining [features](FEATURES.md)
items. Automated tests supplement, but do not replace, these gates. No release, push, production
activation has occurred.
