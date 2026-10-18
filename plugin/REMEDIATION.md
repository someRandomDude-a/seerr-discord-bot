# Review remediation and personal navigation · 18 October 2026

This supersedes the *implementation status*, not the historical reproductions, in
[PROJECT_REVIEW.md](PROJECT_REVIEW.md). Nothing is published or production-activated.
`FeatureParityComplete` remains false pending live-service and companion acceptance.

## Reviewed gaps

| Finding | Change |
| --- | --- |
| R1 · omitted local activity | Schema 13 authenticates the complete ordered activity set, including omissions. A private signed outside-SQLite generation rejects old valid table/seal replay. Removal cannot use unverifiable activity. Legacy state is retained, with removal quarantined for a full configured recent-activity window after upgrade. |
| R2 · forged watchlist intents/baselines | Complete set protection covers native/external/extra watches, preferences, baselines, intents, pinned identities and review archives. Every modifying transaction verifies before changing and checkpoints before committing. Retained unsigned lists need explicit same-account review before reconciliation; approval sends no upstream write, archives the former baseline/intents and never retries old uncertain writes. |
| R3 · reset music/book request states | Complete request journals and private generations authenticate consumed/sending/uncertain/completed states as well as request identity. Resetting only state, deleting rows, or restoring old valid SQLite seals cannot authorize another POST. Identity proofs remain usable only for authenticated read-only history/status. Legacy authorizations cannot confirm. |
| R4 · native routes | Native details use stock `#/details`; controls accept the actual stock route (and the older hash form). Plugin Pages links use its separate `#/userpluginsettings.html` contract, preserving base paths and never adding native credentials. |
| R5 · request/preferences-only users | Bounded scheduled-user enumeration includes native personal preferences and both request journals, independently of Discord linking and watchlist membership. |
| R6 · Discord workflows | Added the controls listed below, retaining native-player/companion/operator boundaries. Real Discord transport and service acceptance are still required. |
| R7 · shipped build gate | CI builds Activity before bundled native tests. Release tests the release-version bundled assembly, then gates artifact upload/publication on exact-ZIP install/restart/native-browser/uninstall/reinstall smoke. The runner checks installed assembly/version and ZIP digest. Workflow changes still need a real GitHub runner execution. |

Workflow actions are pinned to resolved commit SHAs. CI/release also gate known direct/transitive
plugin NuGet advisories and Activity production npm advisories; lookup/report failures are not
treated as clean reports. Build provenance attestation and live-service acceptance remain separate.

Protection is bounded to 100,000 rows / 32 MiB per protected scope. Exceeding a budget blocks;
it never resets or truncates records. SQLite immediate writer transactions serialize generation
publication. A crash between private-checkpoint publication and SQLite commit deliberately fails
closed. Restore a **consistent stopped backup of the entire plugin directory**, including
`hub.db`, the matching key/settings, `state-auth.json` and retained files. Never delete seals,
replace the key, or automatically promote `state-auth.previous.json` to make an error disappear.
Full private-root/key/OS compromise, a valid complete stopped-snapshot restore, native/provider
trust and cross-service concurrency remain boundaries, not problems solved by these seals.

## Watchlist and notification interface

- Native visible top bar: bookmark navigation to a dedicated personal watchlist, and a bell
  opening a keyboard-dismissable notification dialog with an unread count. Jellyfin 12's visible
  toolbar is preferred over its hidden legacy header, using the host's own themed button classes.
  Dialogs use the native `.dialog` surface and inherit host text; the only browser-style reset
  removes the HTML dialog's default `CanvasText`, without supplying any palette or font.
- Native cards/details and shared catalog details offer **Add to watchlist** separately from
  subscription. Saving never forces or erases the user's notification choice or changes favorites.
- Visible movie/show cards/details carry an icon/text removal marker with proposal state, deadline
  where available, and explicit automatic-removal-off wording. A proposal is not a promise of deletion.
- The bell and notification page offer **Allow deletion?** with short **Yes** / **No** buttons for
  current consent notices. Viewing never votes or marks an update read. The shared queue remains
  anonymous; Undo is requester-only and audit retains separate administrator authorization.
  A themed Help icon expands the rules instead of showing lengthy descriptions on every notice;
  Yes uses Jellyfin's primary button class and No uses its neutral button class. No palette is supplied.
- Private DOM, dialogs and counts clear on account/navigation/access loss; successful re-projection
  removes notices that are no longer visible. All mutations remain server-authorized and explicit.
- Global injection requires a compatible **File Transformation** installation; ordinary-user
  plugin-page routing requires **Plugin Pages**. Neither engine is bundled or host files edited.
  [Dependency guidance](WEB_DEPENDENCIES.md) follows upstream/Home Screen Sections reflection and
  minimum-version/API checks, not exact runtime pins. Checksum-pinned 3.0.2.0/Jellyfin 12.2 archives
  are test inputs only. Native smoke now requires automatic injection, ordinary-viewer Plugin Pages
  navigation and zero implicit writes; r8's manually bootstrapped stock smoke remains historical.
  Integration testing exposed two bootstrap issues: File Transformation's exact-key precedence
  shadowed the old regex registration, and Jellyfin's bubbled DIV `pagehide` stopped global controls
  during login. The shared exact `index.html` pipeline and window-transition-only teardown fix these;
  cached browser navigation clears private DOM before re-projecting access on return.
  The later non-root test also exposed duplicated base paths inside `pageUrl`; Plugin Pages now
  receives a server-relative nested resource, while outer links retain the configured server base.
  Consent refresh keeps item-keyed focus/expanded Help, and modal keyboard navigation wraps both ways.

## Added Discord surfaces

| Capability | Private command/button workflow |
| --- | --- |
| Watchlist | Separate Add and Subscribe; revisioned mute/removal; pending-sync inspection and explicit observed-state adoption; retained-list review. |
| Removal | Accessible owned-request choices, explicit proposal confirmation, anonymous paginated queue, Yes/No consent and owner-only Undo. |
| Preferences | `/notifications` independently sets Jellyfin, Discord, mute, own-request inclusion and minimum removal delay; Activity colors remain host-derived/native-managed. |
| Updates | Paginated personal feed, explicit mark-read, consent navigation and scoped delivery diagnostics. |
| Quotas | `/quota` and dashboard permissions/quota display from the current Seerr user context; unknown values are not invented. |
| Real players | All destinations through pagination; previews/confirm/cancel/read receipts; `/players` history and explicit manual-inspection acknowledgement without resend. Activity still cannot operate native players. |
| Messaging | Up to four attachments / 16 MiB total and multiple reviewed announcement channels; inbox kind/guild/channel/peer/query/order filters, complete within-page navigation, rich retained-message details, explicit attachment/image copies, retained replies and mentioned-user conversation navigation; operator delivery diagnostics. No native token or upstream attachment URL is exposed. |
| Parties | Credential-free native handoff; actual player admission, policy/moderation and invitations remain with the unmodified Private SyncPlay contract. No fabricated membership or in-Activity player control. |

Platform interaction limits are handled by bounded pagination. Native-only server settings,
host-derived Activity colors and actual-player/party-owner checks are intentional boundaries, not
permissions bypassed for superficial parity. Recipient-bound party links still need upstream support.

See [ACCEPTANCE.md](ACCEPTANCE.md) for artifact/test evidence and still-open live-service gates.

The r11 baseline passes **338 native / 92 browser-repository tests** and isolated exact-ZIP
install/restart/native-uninstall/reinstall plus dependency-backed mobile/desktop UI smoke. Its
installed/current/packaged assembly bytes match. See the latest selected artifact and new acceptance
evidence in [ACCEPTANCE.md](ACCEPTANCE.md); historical r8 counts are not current completion status.
Production remains untouched and no release/catalog was published. Full live-service sign-off is
still outstanding.
