# Notification polling and optional signed wake-ups

Personal Jellyfin feed/Discord DM preferences, mute and **Include your own requests** are separate
from administrator integrations. Discord DMs require opt-in. Initial snapshots and repeated
identical displayed stages are silent; outages/internal-only stages are not availability events.

The native bell and notification page show **Allow deletion? · Yes · No**, with a Help icon for
consent, activity-window, full-delay, requester/Undo and verified-Arr rules. Details start collapsed.
Jellyfin's themed primary/neutral button classes and dialog/list surfaces supply all colors; no
custom palette overrides skins. Opening notifications or Help never votes, marks read or records
activity. Only the explicit Yes/No buttons submit consent; Yes is not an immediate deletion.

- Subscribed movie/series/music/book watches are checked in bounded rotating batches.
- Existing own Seerr requests are scanned one page per cycle, without creating watchlist entries or
  altering Seerr's request history. Reads/delivery recheck current ownership, native visibility and
  the pinned service/media identity. Old metadata does not become authority after ownership loss.
- Own local Lidarr/Readarr requests use their retained authenticated request identity and current
  configured-service metadata; the latest 200 are scanned in batches of ten. An uncertain add is
  inspected, never resent. Changing its pinned service/edition/identity blocks the old projection.
- The worker has a 45-second cycle budget. Feeds retain 250 events/30 days and expose at most 100
  entries, including at most 20 freshly reauthorized own-request entries per read (ten-second read
  budget). Large histories are not an unlimited real-time notification guarantee.
- Personal delivery status and separately authorized operator totals are explicit read-only
  diagnostics. `sending`/`uncertain` sends are not automatically replayed.

## Optional HMAC protocol

Save a high-entropy **Notification signing secret** (32–256 non-control characters) under
**Signed notification wake-ups**. Leave it absent to disable the endpoint. Empty replacement
fields keep it; explicit removal disables hooks. It is encrypted, not returned or embedded in URLs.

A compatible signing producer sends HTTPS `POST` to the existing Jellyfin base plus
`/MediaHub/Notifications/Hook`. No new listener, runtime, sidecar or application is required.

Headers:

- `X-MediaHub-Timestamp`: canonical Unix seconds (at most five minutes old / 30 seconds future).
- `X-MediaHub-Nonce`: fresh 32 hexadecimal characters.
- `X-MediaHub-Signature`: hexadecimal HMAC-SHA256 using the UTF-8 signing secret over the concatenation:
  UTF-8 `MediaHub.NotificationHook.v1\n<timestamp>\n<nonce>\n`, then the **exact raw body bytes**.

Bodies are limited to 64 KiB. Hints are coalesced to at most one wake-up per minute; nonce memory
is bounded to 1,024 recent entries. Duplicate/invalid/expired signatures fail closed. Nonces are
in-memory: a restart may allow a still-valid recent hint again, which can only cause a verified
read. This endpoint never trusts payload users, statuses, media IDs, service URLs or permissions,
and never authorizes requests, watchlist writes, playback or removal. The independent periodic
poll schedule remains enabled and cannot be starved by wake-ups.

This is a defined signing protocol, **not a claim that generic static-header Seerr/Arr webhook
forms natively implement it**. If the existing producer cannot generate these signatures, keep
polling; do not weaken authentication, put a secret in a query or add a sidecar to imitate support.

Live provider/signing-producer, request-state and Discord delivery acceptance remains required.
