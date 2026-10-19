# Discord Integration · development preview

This is a **separate plugin**, not the watchlist/deletion engine. It requires:

- Jellyfin **12.2.0 / .NET 10**;
- the standalone Watchlist Deletion plugin, public API **1.0**;
- separately installed **Private SyncPlay 0.1.5 / API 1.1.0**.

Plugin GUID: `8456d36a-3c89-4b6d-9959-9a74566cc252`.
The core retains `c70297bf-77bd-4ad4-93c9-f67598519777` and `media-hub/`.

## Install this plugin

1. Install **Watchlist Deletion (Preview)** and its native Web dependencies first.
   Follow the [core-only installation steps](../../README.md#install-the-two-plugins).
2. Separately install the compatible **Private SyncPlay 0.1.5 / API 1.1.0** provider
   from [upstream](https://github.com/someRandomDude-a/private-syncplay-groups).
   The API DLL in the adapter ZIP is not the provider installation.
3. Add the [shared project catalog](../../README.md#catalog-installation-versus-manual-zip-installation)
   once under **Dashboard → Plugins → Repositories**, then install **Media Hub Discord
   Integration (Preview)** from **Catalog**. The core and adapter are separate entries
   in that same catalog; neither is silently installed with the other.
4. Restart Jellyfin. For an optional manual installation, obtain
   **`Jellyfin.Plugin.MediaHub.Discord_<discord-version>.zip`**, not the core ZIP,
   from its matching release and verify its `SHA256SUMS`. Stop Jellyfin and extract the
   entire adapter ZIP to its own `MediaHub.Discord_<discord-version>/` folder inside
   Jellyfin's configured plugins directory. Do not merge it with the core folder or
   remove its bundled SDK DLLs. Restart Jellyfin and verify both project plugins are Active.
5. Open **Dashboard → Plugins → My Plugins → Media Hub Discord Integration (Preview)
   → Settings**. Configure the bot token and server allowlist here; configure the
   matching application ID/client secret for OAuth or Activity. The public Jellyfin
   HTTPS base URL and media-service/deletion settings belong in the core's settings.
   Enable the bot deliberately, using one running instance per token.
6. Users run `/hub-link` and confirm their own accounts in a signed-in native browser.
   Follow [Activity configuration](../ACTIVITY.md) before enabling Activity.

This plugin does not read a development `.env` at runtime. No extra server, runtime
listener or Node/.NET SDK installation is needed for a packaged plugin. Without
the core and provider, gateway/player controls remain unavailable. For native deletion
requests alone, **neither this adapter nor Private SyncPlay is required**.

The shared **Publish Jellyfin plugin repository** workflow now builds and publishes
both plugins into one catalog, preserving both histories. It uses separate `plugin-v…`
and `discord-v…` prereleases and verifies both downloads before deploying the catalog.
These source changes have not published anything: the live entry appears only after
an authorized successful run. The separate **Discord integration CI** still offers an
unpublished **discord-integration-development-only** artifact; unpack that outer
artifact to find the actual plugin ZIP if deliberately testing development builds.
Compatible core and adapter release numbers may differ, but must target the same Jellyfin ABI and core API 1.0.
See the [main installation/upgrade guide](../../README.md#install-the-two-plugins)
for the catalog URL, both folder layouts and retained-state migration precautions.

## Boundary and retained features

Discord gateway/REST transport and native watch-party management now compile in
`plugin/src/Jellyfin.Plugin.MediaHub.Discord/`. Media discovery/requests,
watchlists, removal proposals, consent, personal notifications, opaque controls
and operator message workflows call the [core API](../PUBLIC_API.md). The adapter
does not reference the core implementation assembly or SQLite. It cannot
implement a second deletion rule or bypass current core authorization.

Private SyncPlay remains authoritative for party admission, membership and
playback. Native destination commands are rejected when either player is in a
party, or when either integration dependency is unavailable. Existing native
sessions are required; no Activity-to-native token bridge is exposed.

**The Private SyncPlay SDK dependency is retained.** `/watchparty` first shows the
linked native player's current state. Explicit buttons ask the SDK to create a
private party/invitation or generate an invitation for the caller's owned current
party. The bot returns the **provider's URL**, privately, and can revoke that
invitation through the SDK. It does not redeem links, implement admission, issue
native credentials or infer playback. Invitations remain forwardable bearer
links: signed-in only, one use, no anonymous guests, at most 30 minutes here.
Old Media Hub join buttons/state are not promoted into provider invitations.

## Configuration ownership and safe migration

Open **Discord Integration** in native Jellyfin plugin configuration as an
administrator. The adapter owns its settings API/UI and encrypted file-only state
under **`<Jellyfin data>/media-hub-discord/`**. It has no SQLite dependency. Native
API keys, disabled users and non-administrators cannot administer this page.

Before the first save, signed legacy configuration is reviewed through a purpose-
limited core capability; it is not silently copied or cleared. The first explicitly
confirmed save records custody in the authenticated core root, then writes a new
adapter key and encrypted configuration. The original core key/settings, account
links, message plans, uncertain receipts, invitations and histories remain intact.
Repeated legacy allowlist entries are shown with the same set semantics for review.
Subsequent Discord edits belong to this adapter; the core rejects conflicting edits
from its retained legacy configuration surface. Core service/deletion settings remain
editable independently. Secrets are write-only, with explicit removal controls.

**Back up both plugin directories together while stopped before migrating.** A
torn migration, missing key/settings or missing whole adapter directory disables
the integration instead of resurrecting old credentials or generating replacement
state. Restore a matching stopped pair; never promote individual old checkpoints,
copy a database into the adapter directory, or delete authentication files to retry.
Older builds may reject newly extended authentication roots: downgrades require a
matching whole stopped backup, not rewriting or stripping new fields.

Activity HTTP routes/assets, short-lived memory-only OAuth sessions and Discord
OAuth transport now live in the adapter. Activity uses the public core API for all
native data/actions; no native token crosses into its browser. Native account-link
approval and durable records remain under core custody intentionally. Root/base-path
and proxy tests use disposable loopback TLS, without a permanent test domain.

**Not ready for production:** exact-package host lifecycle, generated-media/Arr,
real native-menu, embedded Discord SDK and broader client acceptance are still
gates. Source compilation and synthetic identity tests are not live parity proof.

## Development

The complete [two-package build commands and workflow/output table](../README.md#build-and-test)
are in the core reference. Build/package this project independently of the core:

```powershell
npm ci --ignore-scripts --prefix plugin/activity
npm run build --prefix plugin/activity
dotnet test plugin/tests/MediaHub.Discord.Tests/MediaHub.Discord.Tests.csproj -c Release
dotnet build plugin/src/Jellyfin.Plugin.MediaHub.Discord/Jellyfin.Plugin.MediaHub.Discord.csproj -c Release
```

Use the packager with `--plugin discord --metadata plugin/repository/discord.json`
and the actual four-part assembly version. Discord ZIPs have their own explicit
allowlist and `discord-v…` release namespace. They contain the public SDKs, never
the core implementation or Private SyncPlay provider. The repository/catalog URL
is unchanged; first adding a Discord entry preserves the existing core catalog.

`.github/workflows/plugin-discord.yml` tests/builds the adapter separately. It
does **not** publish or activate a build. Exact-ZIP dual-plugin restart/uninstall,
real Discord/OAuth/Activity and service acceptance remain required.
