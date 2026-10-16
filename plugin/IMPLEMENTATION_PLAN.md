# Media Hub plugin implementation plan

Updated 16 October 2026. This document defines the plugin's architecture, supported effects
and remaining implementation/verification work. It is not a production-readiness claim.

## Product and architecture

- Target Jellyfin **12 / .NET 10**. The plugin owns its Discord gateway, application services,
  cancellable background workers, authenticated native pages, API routes and bundled Activity.
  It does not spawn processes or install an additional runtime service/admin listener.
- Native Jellyfin user/token/session/library services are authoritative. Discord OAuth proves
  Discord identity only; it never substitutes for native account verification or visibility.
- Seerr owns its users, mappings, request permissions/quotas/history and movie/TV destinations.
  The plugin reads current upstream requests without deleting/recreating them or changing routing.
- Native pages, Discord commands and Activity call the **same application services**. Permission,
  request, ownership, consent and reconciliation rules do not live separately in each transport.
- Personal preferences/subscriptions, elevated integration configuration and messaging-operator
  authority are separate. No administrator credential impersonates a viewer.
- Keep all persistence under `<Jellyfin data>/media-hub/`. Never directly open/attach/migrate host
  databases or write host configuration, network settings or persistent Jellyfin Web files.
- Installation/start/stop/uninstall must not mutate users, libraries, media or playback groups.
  Uninstall retains plugin state. Plugin faults are contained; shared-runtime immunity cannot be
  guaranteed. Do not change global exception, resource, SQLite-pool or host-service policies.

## First-install setup and presentation

- A fresh configuration automatically starts **Set up Media Hub** for a native administrator.
  Present one step at a time with the actual configuration forms, clear manual checks and
  explicit Save actions. Back/Next/Finish never enable a service or claim a verified connection.
- Cover Discord app creation/installation, least-privilege permissions, IDs/secrets, server
  allowlisting, Seerr mapping, optional intents/messaging, OAuth, Activity mapping and account linking.
- Pause/resume/restart must remain available without resetting settings, credentials, links or
  current drafts. Browser-local progress is version/step/completion metadata only, scoped to the
  native user/server/page; never persist credentials or draft configuration there.
- Inherit Jellyfin components, theme and custom CSS. Do not add a separate theme selector.
  Activity is a different origin: copy only validated color preferences, not arbitrary styles,
  fonts, credentials or a privileged iframe of the native host.
- Show one useful status such as **Available**, **Downloading**, **Pending approval** or **Failed**.
  Preserve edition/partial-availability differences where material. Keep detailed diagnostics
  in elevated views; concise wording must not misrepresent an uncertain operation as success.

## Identity and private disclosures

1. `/hub-link` and `/link` issue a short-lived private handoff/code for the requesting Discord user.
2. Native sign-in resumes only the opaque handoff. No raw credential enters a URL, markup or storage.
3. OAuth verifies the matching Discord browser identity using one-use state, optional PKCE and a
   scoped Secure/HttpOnly callback cookie bound to the same native browser/device.
4. Callback GET records proof only. Explicit authenticated POST rechecks both accounts, current
   Seerr context, configuration and expiry, then atomically consumes proof. Conflicts are not overwritten.
5. Forwarded links/codes, wrong identities, stale devices, account changes and crawler GETs cannot
   authorize linking. Correct completed bindings are idempotent; incompatible proofs require a new action.
6. Revalidate live native tokens, enabled accounts and current visibility before private data or
   actions. Native API keys, display-name matches and client-supplied user IDs are not user proof.

## Media, requests, activity and watchlists

- Support accessible movie/TV/music/book library browsing, discovery/search/details, request history,
  permissions/quotas and storage projections without exposing upstream keys or filesystem paths.
- Movie/TV requests use the uniquely mapped Seerr user context and a short-lived owner-bound preview.
  Rebuild outbound payloads from allowed selection fields. Claim once before sending; uncertain
  outcomes are not automatically repeated. Cancellation applies only to an unsent local preview.
- Music/book requests pin one selected album/edition, destination/root/profiles and limits; do not
  request a parent-wide search or mutate an existing unrelated item.
- Only explicit details/open/request/follow interactions may update the linked user's last-played
  timestamp through the supported native API. Preserve completion, progress, play count and favorites.
  Passive lists/polls, notifications, consent, proposals and Undo are not playback or interest.
- Aggregate series/season/episode and edition activity conservatively. An incomplete native scan
  blocks removal rather than concluding there are no recent viewers.
- Bell on ensures watchlist membership plus subscription; bell off stops notifications but keeps
  the title. Remove and global mute are separate explicit operations. Never repurpose favorites.
- Reconcile movie/TV membership with the mapped Seerr watchlist using complete stable reads,
  revisioned intents and three-way baselines. Never infer removals from a partial snapshot or
  blindly retry an uncertain write. Music/books remain plugin-local where no compatible upstream exists.
- Preserve drafts/newer revisions across polls; account changes or lost authorization clear private
  views. Status feeds suppress initial snapshots and duplicates; Discord notifications require opt-in.

## Deletion, consent and cleanup

- Use one durable shared queue with server-side viewer projections. Non-owners see accessible media,
  anonymous ownership, state and deadline—not requester IDs, votes, upstream pins or private audit data.
  Elevated audit is separately authorized. Even an administrator cannot bypass requester ownership.
- Only a live verified original requester may propose removal; only the owner may Undo while eligible.
  Duplicates share the exact target's queue without transferring or disclosing ownership.
- Default lookback is **14 days**. Every current distinct recent viewer must consent to their latest
  activity; missing/declined consent blocks while recent. New activity invalidates earlier consent.
- After unanimity or no recent viewers, start a **full 2-day delay**. Personal increases extend queues;
  configuration/preferences never silently shorten an existing deadline. Re-eligibility after a block
  starts a fresh full delay. Consent/deadline integrity is verified, not inferred from mutable rows.
- Before removal recheck native access/activity, live Seerr ownership, independently approved Arr
  destination and exact edition/provider/entry/path/file identity. Automatic removal defaults off.
- Use Arr file-ID APIs only: no direct media filesystem deletion, root-folder/record/request-history
  deletion, broad commands or stale full-resource PUTs. Reject ambiguous targets and partial ownership.
- Claim durably and authenticate checkpoints. An uncertain DELETE is never resent. Confirm files are
  gone before narrowly scoped monitoring-only cleanup; cleanup retries do not delete files or touch
  a replacement entry. Preserve unverifiable records for reviewed recovery, never bless old consent.

## Messaging, Activity and watch parties

- Messaging operators are explicitly configured Discord identities or deliberately enabled verified
  Seerr administrators, always with a live native link. Channel/member/attachment permissions remain live.
- Keep bounded private inbox metadata/files, explicit retention, normal one-target sends and confirmed
  immutable announcement previews. Authenticate plans/files/delivery states; journal each destination
  before send. An uncertain response is not permission to resend or broadcast more broadly.
- Activity sessions are short-lived and memory-only. Verify app/scopes/current allowed-guild membership
  and the linked native account. Never expose native bearer credentials, host configuration or
  synthetic/native-player impersonation routes through the Activity adapter.
- Use actual native player sessions and supported SyncPlay events. Exact original creator-session
  membership is mandatory for invitations; public participant names are insufficient. With no supported
  proof provider registered, invitations stay blocked. Closing invitations does not eject participants.

## Persistence, resources and release

- Upgrade only recognized plugin schemas transactionally after a verified snapshot. Preserve earlier
  plugin records; reject foreign/future schemas and linked state paths before writes. Missing/changed
  keys and invalid settings fail closed without reset, key regeneration or automatic rollback.
- Use staged/flush-then-rename publication, previous settings, writer leases, revisions, bounded reads,
  per-connection durability and authenticated configuration/authorization records. Keep complete matching
  DB/settings/key backups. Power-loss, filesystem and full-snapshot rollback risks remain explicit.
- Bound JSON depth/bytes/tokens/ambiguity, requests/events/concurrency, native scans, files/database
  growth, network routes/headers/responses and operation/shutdown time. Never hold SQLite transactions
  across network calls. Leave existing network and VPN/DNS assignments untouched.
- Package only allowlisted plugin/runtime files and notices, not host/ASP.NET/SQLite assemblies,
  secrets, tests or developer-machine native binaries. Validate GUID/version/build ABI and checksums.
- A catalog is an HTTPS manifest array, not a Git URL. Publish immutable ZIP assets, actual MD5 and
  SHA-256, version/ABI/source/timestamp and preserved catalog history. Workflow files alone are not
  publication. Verify the downloaded package through real catalog installation before release approval.

## Remaining work and acceptance

[FEATURES.md](FEATURES.md) is the detailed backlog. Remaining gates include native Discord galleries/
navigation, request/notification hooks, watchlist alias/concurrency/recovery cases, Activity polish,
supported exact-session SyncPlay proof and live provider/client acceptance.

Retain native/browser regression coverage for authorization and stale identities, malformed input,
interrupted persistence, schema upgrades, forwarded/replayed proofs, restricted libraries, consent/
aging/delay changes, ambiguous external writes, cleanup-only recovery and tutorial lifecycle. Test the
downloaded ZIP on Jellyfin with companion plugins, base paths, themes and real playback concurrency.

The isolated Jellyfin 12.2 local-ZIP install/restart/native-uninstall/reinstall evidence is limited to
its recorded fixture. Catalog/provider/Discord/companion-client tests remain incomplete. See
[ACCEPTANCE.md](ACCEPTANCE.md), [SAFETY.md](SAFETY.md) and [SECURITY_REVIEW.md](SECURITY_REVIEW.md).
No source cleanup, successful unit test or generated ZIP authorizes publication or production activation.
