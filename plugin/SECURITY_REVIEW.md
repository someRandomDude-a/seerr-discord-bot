# Adversarial-input review · updated 18 October 2026

**Preview hardening, not a security certification or production approval.** Automatic deletion
defaults off; `FeatureParityComplete` remains false. Do not activate production integrations.

## Scope and evidence

The native plugin runtime, browser/Activity boundaries, persistence and packaging were reviewed
and received targeted adversarial regression tests. This is not a claim that every repository
line, dependency, protocol or failure mode has been independently audited. Scope is the plugin's
in-process runtime, its bundled frontend and its release/acceptance tooling. Running-server
evidence and exact test counts are in
[ACCEPTANCE.md](ACCEPTANCE.md).

## Findings addressed

| Boundary / former risk | Guard now in place |
| --- | --- |
| Ambiguous, excessive or malformed JSON | Duplicate/case-alias/escaped-field rejection; byte, depth, token and string budgets for retained/network input. Plugin-only request admission, body/time budgets and sanitized binding failures. |
| Configuration flags/URLs modified outside elevated settings | Format-3 HMAC over settings; live key and revision-rollback checks. Older unsigned formats require an explicit elevated review/save; they cannot activate integrations. |
| Upstream Seerr advertising an unintended Arr destination | Independent administrator approval of the exact service, Seerr instance ID and canonical address before connecting. Strict HTTP(S)/path/header validation, no redirects or shared cookies. |
| Ciphertext copied to another actor/action | Native credentials, request selections, extra-media pins, OAuth handoff proofs, Discord controls and deletion checkpoint states use purpose/identity-bound authenticated encryption. Incompatible old proofs are not blindly replayed. |
| Stored request JSON supplying arbitrary upstream fields | Live mapping/access checks and outgoing payload reconstruction from allowed selected fields; one durable send claim clears reusable request payloads. |
| Altered consent/deadlines authorizing removal | Schema 11 adds a keyed integrity record over each proposal and its ordered votes. Refresh, consent mutation, claim and checkpoint transitions verify/reseal transactionally. Migration never signs old consent. Missing/altered authorization blocks execution; old pre-send proposals remain owner-cancellable. |
| Altered retained messages/attachments or delivery state | Signed actor/configuration-bound previews, attachment metadata/digests, signed per-target state transitions and conditional claims. Single-field state forgery cannot replay a sent message. Retention erases content/attachment manifests while preserving nonce tombstones. |
| Forged Discord controls or OAuth proof fields | Exact-message/actor/expiry-bound control ciphertext, cleared on consumption; same-browser OAuth proof binds state, handoff, native identity, cookie, expiry, configuration and Discord result. |
| Wrong Arr files or broad monitoring writes | Exact provider/entry/path/file identity, bounded scans, approved destination and live ownership/configuration rechecks; file-ID DELETE only. Cleanup scope validated before narrow monitoring-only writes; no stale full-resource PUTs. |
| Host resource and callback failures | Plugin-only network/event/concurrency/operation budgets, bounded file/database/journal reads and connection-local SQLite limits. Malformed companion transforms return the original payload. No global host resource-policy changes. |
| Native command replay/other-user player selection | Schema 12 adds purpose/user/state-authenticated playback previews and conditional send claims. Only actual same-account capable sessions and visible individually playable items qualify; administrator ownership bypass and Activity invocation are absent. Timeouts remain uncertain and retain the bounded slot until completion; receipts never replay commands. |
| Hook body impersonation or artwork SSRF | Optional HMAC/timestamp/nonce-checked HTTPS wake-ups ignore payload authority and trigger notification reads only. Fixed public poster origin, no credentials/redirects/active formats, bounded bytes/dimensions and current access checks. |
| Stale owner moderation UI | Owner-only current-party and fresh snapshot checks before typed SDK policy/moderation calls; account/guest/owner distinctions and explicit shared guest-link effects. No administrator bypass or guest-admission enable control. |

## Upgrade/recovery implications

- Back up the complete plugin directory consistently, including its key, before installing.
- Settings formats 1/2 are review-only. Verify all destinations, privileges and feature flags;
  explicitly save from an elevated native browser. No automatic integration activation occurs.
- Reapprove existing native links yourself; reopen old request/control/OAuth previews.
- Schema-10 and older deletion proposals retain their rows/checkpoints but receive **no**
  authorization seal. Do not synthesize one during recovery. Owner Undo/new proposal is the
  supported pre-send path; uncertain/cleanup records require stopped, operator-reviewed recovery.
- Old unsigned messenger plans/deliveries cannot authorize a send. Keep uncertain outcomes;
  do not reset their states to pending to retry.

## Remaining trust assumptions and release blockers

1. Native Jellyfin authentication, library/user-data APIs and the OS/key store are trusted.
   A process with the plugin key or native-host privileges can forge authorization; these
   guards are not a sandbox. Filesystem path checks cannot eliminate hostile OS-level races.
2. Schema-13 private outside-SQLite generations reject older valid SQLite sets/seals for the
   complete protected activity/watchlist/request scopes. A consistent rollback of the **entire**
   stopped directory (including matching private roots/checkpoints/key) is not detectable solely
   with local state. Restore only during stopped maintenance; never reseal a mismatched subset.
3. Watch baselines/intents, subscriptions/preferences and request authorization states are now
   complete-set authenticated. Notification/inbox projections still rely on private state plus
   live identity/visibility/schema/domain checks; they are not write authority. A hostile private
   state writer can affect unsealed projections. No comprehensive adversarial-state certification
   is claimed; full private-root/key compromise remains outside this integrity boundary.
4. Approved provider addresses and their responses remain a trust boundary. DNS/proxy changes,
   malicious trusted providers and compromised native metadata cannot be made harmless merely
   by validating JSON. Private network addresses intentionally remain supported; no network or
   VPN/DNS assignment is changed by this plugin.
5. Jellyfin/Arr lack a shared transaction between the last activity/access check and remote
   mutation. Real-service concurrent playback/deletion, restart/power-loss and permission-loss
   acceptance remain mandatory. No direct media filesystem deletion is permitted.
6. Shared-runtime exhaustion, native crashes, dependency vulnerabilities and actual filesystem
   durability cannot be excluded by unit tests. Maintain host backups/resource monitoring.
7. Live Seerr/Arr/Discord/OAuth/proxy/companion-client acceptance and remaining [features](FEATURES.md)
   work are incomplete. Watch parties use the reviewed public Private SyncPlay SDK, not a duplicate
    engine. Invitation bearer URLs are forwardable; native-control handoff is implemented without
    those secrets, but audience-bound invitations need provider support. Real-media/multi-player
    acceptance remains outstanding. No anonymous guests can be enabled through Media Hub.
8. Hook nonces are bounded in-memory hints, not durable write authorization. Restart may permit an
   otherwise valid recent hint again; it can cause only an extra verified read, not a replayed action.
   Native player and owner-policy checks are not atomic transactions with external player/provider
   state changes; actual concurrent-client acceptance remains mandatory.

No release, catalog publication or production deployment is authorized by this
document. See [SAFETY.md](SAFETY.md) for permitted effects and [README.md](README.md) for recovery.
