# Operation safety boundaries

Only the plugin's `<Jellyfin data>/media-hub/` directory is opened for persistence. No host
database/schema/configuration or persistent Jellyfin Web edits are permitted. Uninstall retains
plugin state; it does not delete upstream requests, libraries or unrelated application state.

Configuration, user input and external/retained data are untrusted. Settings format 3 is
authenticated; formats 1/2 are review-only until an explicit elevated save. Every discovered
Radarr/Sonarr destination additionally needs independent service/instance/address approval.
See [SECURITY_REVIEW.md](SECURITY_REVIEW.md) for findings, upgrade incompatibilities and remaining
trust assumptions; this review does not certify all repository code or every failure case.

| Operation | Verification and permitted effects |
| --- | --- |
| Install/start/stop | Register plugin services/optional response transforms; open only plugin state; no user-data, library, user or SyncPlay mutation. Background personal work starts after an interval, for existing users. |
| Read/poll/feed | Native account/access checks and allowlisted projections. No simulated playback, completion, favorites or last-played writes. |
| Explicit details/open/request/follow | Resolve the accessible item; journal local interest first. Only `LastPlayedDate` is passed to the supported user-data save API. Preserve completion/progress/count/favorites; never replay an uncertain host write. |
| Seerr request | Verify unique native mapping, pinned origin/account and live `auth/me` context. Confirm a short-lived owner-bound plan; send only the selected media/edition/seasons through `X-API-User`. Seerr enforces quotas/permissions/defaults. One atomic send claim; uncertain/sending receipts cannot resend. Cancel only an unsent local plan, not an upstream request. |
| Watchlist sync | Same pinned user context; two complete matching reads before inferring removals. Three-way baselines plus explicit revisioned intents; union first sync, never replace either side with a partial snapshot. Only own membership endpoints are written—no media request, deletion, monitoring or favorites changes. Ambiguous writes need read reconciliation or a new explicit user choice, not automatic replay. |
| Consent | Private recipient/account/current-activity binding; another user cannot use the notice. Proposal/deadline/vote integrity is checked transactionally; schema upgrades never sign old consent. Fresh activity invalidates old consent. Silence/decline blocks until aging out. Never record interest for consent/deletion operations. |
| Arr removal | Admin opt-in defaults off. Live original-requester identity/ownership, consent/delay/access and pinned exact edition/destination/path/provider/entry/file identity. Only Arr file-ID deletion APIs; **no direct media filesystem operations**, movie/series record deletion, root-folder deletion, request-history deletion or arbitrary Arr commands. |
| Monitoring cleanup | Only after durable confirmation that pinned files are gone. Recheck origin/entry-added/provider/path and reject replacements. Narrow movie/series editor and Sonarr season-pass monitoring APIs; never PUT an old full resource over current paths/profiles/tags. Absent entries stay absent. Failed cleanup retries only monitoring, never file deletion. |
| Discord send/control | Live verified identity/access plus channel opt-in/mute for personal DMs; at-most-once journal before send, no uncertain-send retry. Media controls are opaque, expiring, exact-message/both-identities bound and invalidate an old view together. No allowed mentions. Public party messages contain no private details. |
| Party create/join | Actual authenticated player and native SyncPlay policies/events only. No synthetic bot session. Invitation authorization requires exact original creator-session proof; the default absence of this proof blocks invitations. A persistence failure never blindly leaves a player that might have switched groups. |
| OAuth link | Prove both identities in the same browser using native token/device binding, one-use state, optional PKCE and a scoped Secure/HttpOnly callback cookie. GET never links; explicit authenticated POST rechecks Seerr/native/configuration and atomically consumes the proof. Only an opaque handoff may survive login in session storage. |
| Optional Activity | Memory-only short-lived Discord sessions; verified application/scopes/allowed-guild membership plus live linked native authentication. Native API keys and client-supplied user IDs are not proof. No Jellyfin bearer leaves the server, no host configuration/SyncPlay impersonation routes, and SDK account changes clear private state. Only validated color values cross the theme boundary. |
| Operator messenger | Independent explicit Discord/verified Seerr operator permission, live destination checks, bounded files, authenticated actor/configuration-bound previews and attachment digests, and per-destination authenticated send claims. An ambiguous send is not replayed. Retention removes content/file manifests but preserves idempotency tombstones. |

Fault-injection/unit/browser tests do not prove live service compatibility, power-loss durability,
native user-data concurrency or immunity from shared-runtime/plugin resource failures. Arr APIs
also lack a cross-service atomic transaction with the final Jellyfin activity scan: real-service
concurrency acceptance is mandatory. Never describe this preview as safe for production merely
because mock tests pass. No destructive worker is enabled automatically by a schema upgrade.
