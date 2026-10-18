# Project review · 18 October 2026

**Historical pre-remediation review.** The findings below describe r4. Subsequent implementation
and its remaining acceptance boundaries are tracked in [REMEDIATION.md](REMEDIATION.md); do not
read this original evidence as a claim that the later changes were already tested here.

**Verdict: not ready for security, full theme-compatibility or Discord-parity sign-off.**
This is a review of the current uncommitted source and r4 acceptance evidence, not production
approval. No runtime/workflow fixes were made during this review. Existing work was preserved.

## Confirmed findings, ordered by impact

### R1 · High · Local activity omissions can weaken deletion consent

Sources: `src/Jellyfin.Plugin.MediaHub/MediaActivity.cs:50–57,91–97` and
`src/Jellyfin.Plugin.MediaHub/DeletionQueue.cs:119–127`.

Explicit interactions are journaled in the unsigned `activity` table before a best-effort native
last-played update. If that native update fails, the journal may be the only record protecting a
recent viewer. Removing the journal row makes `Read` omit that viewer. The deletion queue feeds
this projection into its consent policy; signed proposal/vote records do not authenticate the
completeness of the separate activity input.

Isolated mock reproduction: force native timestamp persistence to throw, record an interaction,
then remove only its plugin activity row. The recent-viewer count changes **1 → 0**, with the key
unchanged. No actual deletion was attempted. This finding assumes a writer to plugin SQLite state,
not a remote unauthenticated caller or possession of the signing key.

Required fix: protect activity completeness/omissions as well as row contents, and block deletion
when locally recorded protection cannot be verified. Row MACs alone cannot detect removed rows.
Add consent-policy/execution regressions for omitted or rolled-back local-only activity.

### R2 · High · Unsigned watchlist records authorize outbound mutations

Sources: `src/Jellyfin.Plugin.MediaHub/WatchlistSync.cs:213–238,254–274` and
`src/Jellyfin.Plugin.MediaHub/MediaMaintenance.cs:35–37`.

Domain checks on retained `watchlist_intents` validate IDs, booleans, revisions and state names,
but not the authenticity of the user's intent. A database writer without the key can insert a
valid-looking `ready` intent. Normal synchronization claims it and sends the scoped Seerr mutation.
Unsigned local/baseline membership can also influence generation of new ready intents.

Isolated mock reproduction: insert one valid movie intent, never call Follow, run the same
`Sync(..., importNative: false)` used by maintenance: **one outbound write** occurs. Live user and
visibility checks still run; the defect is missing intent authorization, not cross-account access.

Required fix: authenticate intent identity/mapping, desired membership, revision and consumed state,
and the completeness of local/baseline inputs that can synthesize intents. Unverifiable legacy state
must remain retained and review-only rather than automatically becoming outbound authorization.

### R3 · Medium · Music/book request proofs are reusable after a state-only reset

Sources: `src/Jellyfin.Plugin.MediaHub/ExtraMedia.cs:110–111,144–180`.

The encrypted pin authenticates request identity/destination/expiry, but not the mutable request
state. Unlike movie/TV send claims, the music/book claim keeps its reusable authorization proof.
If an already added item is subsequently absent upstream, resetting `completed` to `prepared`
allows confirmation of the **same request ID** to issue another add before its original expiry.

Isolated mock reproduction: successful request, simulate external removal, change only retained
state, reconfirm the same ID: posts change **1 → 2**, with the key unchanged. The existing-item
guard correctly blocks the second add while Arr still lists the item; that guard does not make
the consumed authorization one-use after an upstream removal or inconsistent absence.

Required fix: separate read-only request-identity evidence from one-use write authorization;
consume the latter atomically or authenticate terminal state transitions. Cover cancellation,
uncertain outcomes, upstream absence and replay without erasing upstream history.

### R4 · Medium · Native detail controls reject Jellyfin's normal route

Sources: `src/Jellyfin.Plugin.MediaHub/Web/items.js:36–43`,
`src/Jellyfin.Plugin.MediaHub/Web/hub.js:126,156–161`,
`src/Jellyfin.Plugin.MediaHub/DiscordMedia.cs:130,196`, and
`web/tests/items.test.js:9`.

Read-only browser probing on the owned stock Jellyfin 12.2 fixture shows that
`Dashboard.navigate('details?...')` generates **`#/details?...`**. The item-control extension
requires **`#!/details?...`**, so it returns before verifying/injecting native detail controls on
the normal route. Its test constructs the old route rather than exercising the native router.
Several native/Discord opening links also emit the old hashbang form and need native-router tests.

Required fix: use/reconcile the host's supported routing and base path, test host-generated detail
URLs, and separately verify the Plugin Pages linking/handoff route. Do not infer companion routing
compatibility from a stock administrator-settings smoke test.

### R5 · Medium · Request-only native users are omitted from notification maintenance

Source: `src/Jellyfin.Plugin.MediaHub/MediaMaintenance.cs:27–41`.

The scheduler enumerates linked users and watchlist tables, but not personal preferences or request
history. A native-only user can enable own-request updates and submit requests without a Discord
link or watchlist entry, yet never be selected for automatic notification scans. Direct `Scan`
tests do not exercise this worker selection.

Isolated mock reproduction: save personal own-request preferences and complete a native request
without linking Discord or following anything; the worker's enumeration returns **zero users**.

Required fix: include the appropriate known personal/request users in bounded worker enumeration,
preserve active-native-account checks, and test scheduling rather than only invoking the feed
directly. Linking Discord must not become a prerequisite for native own-request notifications.

### R6 · Medium · Discord capability parity is incomplete

Sources: `src/Jellyfin.Plugin.MediaHub/DiscordMedia.cs`, `DiscordOperators.cs`,
`DiscordExtraMedia.cs` and `ActivityController.cs`.

| Capability | Slash commands / private buttons | Discord Activity |
| --- | --- | --- |
| Library/search/discovery/details | Implemented, bounded navigation | Shared catalog UI |
| Movie/TV seasons, standard/4K and music/book requests | Implemented previews, confirmation and receipts | Shared request UI |
| Watchlist bells/remove/sync | Implemented | Implemented |
| Reviewed uncertain-watch recovery | No Review/AcceptRemote controls | Implemented shared routes/UI |
| Removal queue, consent and owner Undo | Implemented | Implemented |
| Create a removal proposal from an owned request | No proposal action/command | Implemented |
| Independent notification preferences, own-request opt-out and removal delay | Only coarse `/notifications enabled` toggle | Shared personal preferences |
| Request quota/permission display | No corresponding quota action | Implemented |
| Real native player selection/play previews/receipts | Partial: first 18 destinations; no history/acknowledgement workflow | Native-player operations intentionally denied |
| Private SyncPlay management | Native-controls handoff, not in-Discord moderation | Native-controls handoff |
| Operator chat/announcements/files | Implemented, narrower target/file controls | Richer shared composer |
| Rich inbox/retained replies/attachments/conversation navigation | Text-only inbox rendering | Rich shared messenger |
| User/operator diagnostics | No equivalent diagnostic controls | Shared diagnostic routes/UI |

Required fix: define the required surface per capability and implement missing authorized user
workflows. Do **not** achieve superficial parity by giving Activity native credentials, fabricating
players, bypassing companion admission or treating administrator status as operator/owner authority.
Native-only server configuration and actual-player boundaries are legitimate design constraints,
not permission checks to remove. Full recipient-bound party invitations need a provider contract;
the present SDK supplies forwardable bearer invitations.

### R7 · Medium · CI tests do not cover the shipped bundled assembly/native UI

Sources: `.github/workflows/plugin.yml:24–41`,
`.github/workflows/plugin-release.yml:49–61` and
`src/Jellyfin.Plugin.MediaHub/Jellyfin.Plugin.MediaHub.csproj:22–23`.

CI's native job tests before the Activity exists, in a separate job from its build. Release also
runs native tests before building Activity and only then builds the release-version bundled
assembly. Neither path tests with `RequireActivityBundle=true` after bundling, and neither runs the
stock-browser or installed-ZIP smoke harness. An Activity build can pass while assembly/resource
integration or real Jellyfin component behavior remains untested in the release gate.

Required fix: build Activity first, test the same release-version/resource configuration, then
smoke-test the exact generated ZIP on an isolated latest-stable host/browser before publication.
Keep immutable history, downloaded-byte verification and least-privilege job permissions.

## UI/theme assessment

- Native pages use Jellyfin's component imports, generated labels, containers, native button/list
  classes and no competing native stylesheet/palette/font. R4 corrected v0 dynamic-field creation.
- Stock Jellyfin 12.2 / Edge settings tests pass desktop and 390px-wide layouts, Help, focus, draft
  preservation/teardown and zero implicit settings saves. The route probe repeated those checks.
- This is **theme-friendly structure**, not certification of every theme or client. Detail routes
  still need R4 fixed; posters, companion controls, keyboard/screen-reader use, non-root base paths
  and actual supported third-party themes need a real acceptance matrix.
- Activity is an isolated Discord document. Its fallback CSS and four-color bridge do not inherit
  a full Jellyfin theme. Keep that limitation explicit rather than relaxing CSP/origin boundaries.

## Security/workflow positives and remaining gates

Purpose-bound AES-GCM proofs, signed elevated settings, current native access checks, separate
operator authority, requester-only deletion, reviewed Arr destinations, plugin-only persistence,
retained uninstall state, one-use messaging/player claims and the public companion-SDK boundary
are valuable safeguards. They do not authenticate the omitted inputs described above.

Workflow lint passes. Release jobs use scoped permissions, validate release versions/latest stable
ABI, preserve catalog history, refuse version replacement and verify published ZIP bytes. Further
supply-chain hardening should pin third-party actions to commit SHAs and automate dependency audits
and provenance; mutable action tags are not content pins.

Read-only advisory checks during this review reported **no known vulnerabilities** for Activity
production npm dependencies or plugin direct/transitive NuGet dependencies. This is not an audit of
Jellyfin itself, the provider implementation, every development dependency or unknown vulnerabilities.

The existing 317-native/79-browser passing baseline does not cover the adversarial scenarios above.
Additional temporary mock probes reproduced R1–R3 without changing repository source or signing keys.
The native route probe used only the labelled `mediahub-test-settings` fixture and a nonexistent
media ID, issued no settings POST/media action, and stopped the fixture afterward. No production
containers, credentials, media, services, DNS/VPN assignments or published artifacts were changed.

## Recommended order

1. Fix R1/R2 authorization/completeness and add adversarial migration/omission tests.
2. Consume music/book write proofs safely (R3), retaining read-only identity/history.
3. Correct/test native routing and request-only scheduling (R4/R5).
4. Agree and close the explicit Discord surface matrix (R6).
5. Gate releases on bundled-assembly and exact-ZIP/native-browser checks (R7), then finish real
   Seerr/Arr/Discord/OAuth/companion, restricted-library, concurrent-player and third-party-theme acceptance.

Keep `FeatureParityComplete` false and automatic removal disabled pending these fixes and gates.
