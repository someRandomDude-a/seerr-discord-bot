# Watchlist Deletion public API 1.0 · development contract

The standalone plugin keeps Media Hub's existing GUID, assembly and retained
`<Jellyfin data>/media-hub/` state. The Discord adapter depends only on
`Jellyfin.Plugin.WatchlistDeletion.Api.dll`, not the core implementation.

## In-process capability, not an HTTP impersonation endpoint

Install the public SDK alongside an integration and register
`services.AddWatchlistDeletionClient()`. Resolve `IWatchlistDeletionClient` and
check `IsAvailable`. The SDK uses a **keyed BCL delegate and bounded JSON**, so
two Jellyfin plugin load contexts do not have to share DTO/interface identity.
Calls are versioned by `CoreApi.ServiceKey`; no assembly scanning, native API
key, extra listener or direct store access is needed.

```csharp
var actor = await client.InvokeAsync<LinkedActor>(
    discordSnowflake, null, "identity.resolve", cancellation: cancellation);
var queue = await client.InvokeAsync<QueueItem[]>(
    discordSnowflake, actor.User, "deletions.list", cancellation: cancellation);
```

DTOs are in `Jellyfin.Plugin.WatchlistDeletion.Api.Contracts`.

**Trust boundary:** this is for installed, trusted in-process integrations. The
transport must prove the external subject using Discord's authenticated gateway
or verified OAuth; never forward a caller-supplied subject or operation from an
anonymous HTTP request. Another installed plugin already runs with server
process privileges; the service key is a capability locator, not a secret or a
sandbox. Transport-only operations can retrieve bot credentials and ingest
gateway events. They must never become general user endpoints.

The core resolves the retained, native-approved subject binding and validates
its Jellyfin token/device live for **every user operation**, including reads.
`ExpectedUser` detects relinking; it is never authority. Native credentials,
keys, SQL connections and administrator impersonation are not returned to the
Discord/Activity user. Core service methods recheck visibility, Seerr ownership,
consent, delay and exact target proof; the adapter must not reproduce those rules.

## Operation families

- `identity.resolve`: linked native actor/session identity, never a native token.
- `personal.*`, `watch.*`, `media.*`: scoped preferences, watches and explicit
  interactions. Passive reads do not record activity.
- `catalog.*`, `requests.*`, `extras.*`: scoped discovery and once-only confirmed
  upstream writes/receipts.
- `deletions.list/notices/owned/propose/proposeNative/permission/status/respond/undo`: ordinary anonymous queue
  projections and the caller's own actions. No execution or admin-audit RPC.
- `notifications.*`: only the linked caller's feed/read markers/diagnostics.
- `players.*`: real caller-bound native sessions, explicit previews and receipts;
  an integration/provider guard is mandatory before any send.
- `operator.require`, `messages.*`: fresh operator authorization and existing
  durable previews/receipts. Native administrator status is not operator authority.
- `transport.*`, `delivery.*`, `controls.*`, `parties.review`: trusted adapter
  plumbing, retained-link migration and purpose-limited review signing.

The adapter also supplies optional `oauth.exchange/verify/revoke` transport. The
core owns retained native link/session proof and state; it no longer performs Discord
OAuth network calls itself. `transport.oauthConfiguration` is a trusted integration
capability containing application credentials, never an ordinary user operation.
The adapter owns encrypted file-only configuration and Activity HTTP/assets/OAuth
sessions. Native approval and durable records remain under core custody. Trusted
`transport.legacyDiscordConfiguration`, `transport.confirmDiscordMigration` and
`transport.discordMigrationState` support explicit administrator migration, never
ordinary user endpoints. The core root remembers migration so missing adapter files
cannot silently revive old credentials. Snapshot access uses a separate bounded BCL
delegate; it never recursively retrieves runtime configuration through the core API.

`identity.profile` is a purpose-limited linked/unlinked projection for an already
verified external subject. Linked reads reverify the native token/device and protected
personal state. Activity still cannot choose an arbitrary native actor or invoke
generic administrator/SQL operations.

See `CoreApiProvider.cs` and `Contracts.cs` for exact payload fields. These remain
a development contract until dual-plugin/native-load-context acceptance passes.
Administrator monitoring stays on separate elevated, native-user HTTP endpoints
and must not be added to ordinary Discord queue DTOs.

## Failure handling

Eight bounded outstanding calls, a 30-second linked budget, bounded JSON/results
and sanitized `CoreApiException` errors contain failures. If a provider ignores
cancellation, its slot stays occupied until it finishes. A timeout means
**uncertain**, not unsent. The SDK does not retry, undo or replace any write.
Read the retained receipt/current state before a further user decision.

Absence or incompatibility yields `core_unavailable`; saturation yields
`core_busy`. Unknown operations, disabled accounts, revoked native sessions,
relinking, corrupt state and authorization loss fail closed. No failure causes
fresh state, replacement keys or automatic reapproval.
