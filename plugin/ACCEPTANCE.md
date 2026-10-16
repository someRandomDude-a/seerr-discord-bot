# Isolated Jellyfin acceptance · 16 October 2026

This is limited smoke-test evidence, **not feature-completion or production certification**.

## Tested package and host

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
tooling. Current checks pass **249 native tests on each API version** and **57 browser/repository
tests**, including the integrated tutorial lifecycle, plugin-only source/workflow policy and
local documentation links. The bundled Activity builds and both active GitHub workflows pass
`actionlint` validation.

These source checks do not replace the recorded host lifecycle evidence above. The first-run
tutorial and subsequent repository cleanup were not installed into that recorded package;
a fresh package's host lifecycle and catalog/provider/client acceptance are separate gates. Retained test
artifacts/checksums and runtime state were not rewritten by source cleanup.

A fresh plugin-only baseline package was built with no warnings/errors at
`plugin/artifacts/plugin-only-20261016-r1/repository/Jellyfin.Plugin.MediaHub_0.1.0.0.zip`.
Its nine-file runtime/license allowlist, catalog ABI/MD5 and independent SHA-256 were checked:
`46e2986a620f910b9f3cdf157165fe9f79aaef70f21144d7250c62d0fb10d4d9`.
It is local-only, not published or installed by this cleanup.

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

## Reproduce safely

`plugin/tools/Test-Jellyfin.ps1` modes: `Start`, `Configure` (resume owned partial setup),
`Install -Package <ZIP>`, `Check`, `Restart`, `Uninstall`, `Stop`. The script refuses foreign
containers, existing installation targets and unrelated retained setup state. Credentials
remain in the user's approved temporary directory, outside the repository and command output.
`Stop` retains test volumes/credentials; no broad cleanup is performed.

## Still required

Published catalog/download installation; populated older-schema authorization/recovery acceptance
(the owned empty-integration fixture upgrades to schema 11, but is not a real pending-work fixture);
compatible Plugin Pages/File Transformation; actual restricted libraries/editions and playback
concurrency; live Seerr/Arr requests/deletion/unmonitoring; Discord OAuth/cookies/proxy, operator
messaging and rate limits; multi-browser exact-session SyncPlay; remaining [features](FEATURES.md)
items. Automated tests supplement, but do not replace, these gates. No release, push, production
activation has occurred.
