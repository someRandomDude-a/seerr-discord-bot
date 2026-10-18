# Private SyncPlay watch parties

Media Hub uses the supported **Private SyncPlay API SDK 1.1.0**, released with provider
**0.1.5**, on the **latest stable Jellyfin (currently 12.2.0 / .NET 10)**. Playback, membership,
join confirmation, media restrictions and admission belong to Private SyncPlay, not Media Hub.

Upstream contract: https://github.com/someRandomDude-a/private-syncplay-groups/blob/main/docs/plugin-api.md

The reviewed release is pinned to commit `9d86639a2155f3092fa47d0ee7175e92d5113927`.
The public SDK DLL/XML and license are retained in `lib/PrivateSyncPlay.Api-1.1.0` with
[release provenance/checksums](lib/PrivateSyncPlay.Api-1.1.0/README.md). Only the API DLL ships
in the Media Hub runtime ZIP. Install the provider separately; it is never bundled or auto-installed.

## Setup and native workflow

1. Install compatible **Private SyncPlay 0.1.5** on the isolated/test Jellyfin 12.2 host, restart,
   and configure its own permissions. Media Hub does not alter its configuration or enable guests.
2. In Media Hub's administrator settings, save the trusted public **HTTPS Jellyfin base URL**,
   including a base-path prefix if used. No client-supplied URL or untrusted Host header is used.
3. Open Media Hub on that public HTTPS origin, in your existing signed-in Jellyfin player.
   Select **Refresh party status** under **Watch together**. Missing, disabled, incompatible and
   unhealthy providers produce a contained error; other Media Hub features remain usable.
4. **Create private party and invitation** uses the provider's atomic `CreatePartyWithInviteAsync`.
   Visibility is explicitly private; capacity inherits administrator policy. An already joined
   player must use its existing party rather than silently creating/replacing another.
5. An owner may generate another signed-in invitation or revoke a displayed one. Invitations have
   **one redemption**, request expiry within **30 minutes**, and **AllowGuests: false**. Provider
   limits still apply; disallowed values are rejected, never silently increased. Links are bearer
   secrets and **can be forwarded**: share deliberately. They are not bound to a Discord recipient.
6. The link opens the companion's `/PrivateSyncPlay/Watch` flow. Joining and group playback remain
   in that plugin. Media Hub does not claim playback/join success
   from HTTP acceptance. **Leave** and owner-only **End** require explicit confirmation; ending
    removes everyone, while the final signed-in member leaving invalidates all guest access.
7. **Load owner controls** explicitly fetches an owner-only policy/participant snapshot. Owners can
   pause joins, narrow playback/queue ceilings, adjust capacity within provider limits, set non-owner
   account permissions, remove/block an account and later unblock it. A guest removal explicitly
   confirms revocation of the **shared admission link and all its guest leases**; it is not a human ban.
   Guest admission cannot be enabled here. Disabling existing guest policy revokes guest-enabled links.
   Every mutation refetches/checks the reviewed snapshot; reload after changes. The SDK has no atomic
   cross-client compare-and-swap, so provider concurrency/ceilings remain authoritative.

## Boundary and failure behavior

- `WatchPartyIntegration` depends only on the public `IWatchPartyService` plus Media Hub's trusted
  settings/native identity checks. Registration calls `AddPrivateSyncPlayClient()` without resolving
  services or contacting the provider during registration/startup. Its SDK handles Jellyfin's separate
  assembly contexts. No raw keyed-delegate protocol, reflection, main implementation DLL reference,
  loopback HTTP, synthetic player, guest credential or duplicate SyncPlay engine is used.
- Native `/MediaHub/WatchParty` routes authenticate the live user token/device, recheck an enabled
  account and its actual connected session, and apply native SyncPlay policies. Actor IDs never come
  from request bodies. Every party action refetches that exact session's current party and checks the
  expected party ID. Invitation/revoke/end require ownership even for a Jellyfin administrator.
- `Status.WatchParty.Available` means **registered**, not healthy. `/MediaHub/WatchParty` performs
  the real current-party check. Polling is conservative (the page's 10-second refresh), has no writes,
  and never returns the SDK's join code, owner ID, participant IDs or guest credentials. The separate
  explicit owner-management route returns only bounded policy and opaque party-scoped participant IDs/names.
- Provider calls have a 10-second cancellation budget and one bounded execution slot. If a provider
  ignores cancellation, its slot remains held until it finishes; other host services are not awaited
  behind it. Errors are mapped to safe fixed messages and appropriate 400/403/404/409/429/503 statuses,
  never arbitrary provider exception text. Calls are not automatically retried or compensated.
- A timeout/failure may occur **after** provider mutation. The browser disables further writes until
  explicit refresh, and refetches never replay a write. Check the companion before acting again.
  A lost invitation response cannot recover its secret or ID through this API; use the companion to
  review/end the party rather than blindly generating replacement links. This is not exactly-once delivery.
- At most four newly generated links are retained in the current page's memory/DOM; navigation,
  identity loss, error, expiry or party change clears them. No URL/token/code is persisted to plugin
  state, browser storage, history, logs or Discord. Returned link origin/base path, fragment, limits
  and guest flag are verified; native UI adds no independent stylesheet.
  Link clicks/middle clicks/context menus also check the current local account before navigation,
  not just the JSON request path. Permission denial in this optional feature does not hide other media.
- Activity cannot invoke these routes or manage a native player; its watch-party controls are hidden.
  The old `/watchparty` Discord publishing flow is not revived because this SDK's bearer links do not
  supply the previous audience-bound invitation semantics. Old Discord buttons receive a retired-flow
  explanation. Discord's private dashboard and Activity now provide an explicit **native controls**
  handoff. It sends no bearer URL, creates no player and makes no join/playback claim. Recipient-bound
  Discord invitations still require provider support absent from the current bearer contract.
- Legacy `/MediaHub/Parties` routes remain unavailable tombstones. `hubTicket` fragments are scrubbed
  without redemption. Schema 12 retains old party/invitation/ticket rows intact but never uses them
  as new provider IDs or authorization. Normal `hubLink` account linking remains independent.

## Verification limits

Automated tests cover the typed adapter, missing/unhealthy providers, exact actors/ownership,
hostile replies/URLs, bounded cancellation, uncertain writes, page teardown and SDK packaging.
Real two-plugin load-context/lifecycle smoke evidence is recorded in [ACCEPTANCE.md](ACCEPTANCE.md).
Two real Edge clients now pass loopback native admission, decoded generated-media group playback,
owner/admin boundaries, permission/stale-review controls, kick/block/unblock, invitation revocation,
fresh admission and end through the separately installed unmodified provider. The scenario passes
independently after restart. It does not certify public HTTPS/proxy invitation opening, all expiry/
capacity/disconnect/library-permission races, other clients/skins or live Discord/Activity handoff.
Shared-runtime provider/OS compromise cannot be sandboxed by an in-process plugin.
