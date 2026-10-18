# Install and configure the Jellyfin plugin

Media Hub targets only the **latest stable Jellyfin (currently 12.2.0)** on **.NET 10**. Users install the plugin ZIP/catalog package,
not a standalone application or development server. The optional Discord Activity is bundled in
the plugin and uses the existing Jellyfin listener. No additional admin port is required.

## Before installation

- Use a test Jellyfin and a separate test Discord application/server. The preview is incomplete
  and not approved for production; automatic deletion must remain off during onboarding.
- Back up native Jellyfin data and any existing plugin state consistently before maintenance.
- Have a native Jellyfin administrator account and an ordinary account for permission checks.
- Have an existing user-facing HTTPS Jellyfin base URL. The plugin does not create hosting,
  change VPN/DNS assignments or expose services for you.
- For movie/TV requests, configure Seerr's Jellyfin user mappings, request permissions/quotas
  and existing media-service routing. Seerr requests/history remain intact.

## Install through Jellyfin's repository settings

After publication, add the manifest URL under **Dashboard → Plugins → Repositories**:

```text
https://somerandomdude-a.github.io/seerr-discord-bot/manifest.json
```

This is the intended catalog URL; source changes do not publish it. Confirm the URL returns a
Jellyfin manifest array and its version's ZIP exists before installing. The manifest specifies
the plugin GUID, version, target ABI, release URL, checksum and timestamp. See
[publishing](../plugin/README.md#publish-the-repository) if you maintain the repository.

Install **Media Hub (Preview)** from the catalog and restart Jellyfin. For local development,
build the documented allowlisted ZIP and use the owned disposable-host harness; do not install
the test project's output or replacement Jellyfin/SQLite assemblies.

For ordinary-user navigation/login resume and supported Web-card controls, install Jellyfin-12
compatible **Plugin Pages** and **File Transformation** builds (see [dependency guidance](../plugin/WEB_DEPENDENCIES.md)). The elevated configuration page
does not require promoting ordinary users or giving Activity configuration access.

## Configure connections and permissions

Sign in as the administrator and open **Dashboard → Plugins → Media Hub → settings**.
Choose a **Settings section**, use field **Help**, and load named folders/profiles from saved
service connections. Review the structured list of independently approved media servers.

Only **Save server settings** changes configuration across all sections. Section/help navigation
never saves or enables a service. A save does not certify a connection. Complete permission,
linking and read-only checks with an ordinary account. See [settings controls](onboarding.md) and the
[field-by-field reference](../plugin/ONBOARDING.md).

Discord connects outward using the application's bot token; leave Interactions Endpoint URL
blank. Use a server install with `bot` and `applications.commands`, limited channel permissions
and an explicit allowed-server list. Administrator permission on the Discord account is not
required. OAuth client secrets are separate from bot tokens; Activity connection help covers both
redirects, directory URL mapping, human launch permissions and Discord's App Launcher.

## State and updates

All plugin persistence is under **`<Jellyfin data>/media-hub/`**: database/journals, settings,
matching key, locks and upgrade snapshots. Back up the complete directory consistently. Protect
permissions/ACLs. Losing a key or mixing snapshots blocks access rather than resetting state.

Settings formats 1/2 require an elevated review and explicit save before integrations can run.
Schema upgrades preserve records; unverifiable historical consent/proofs are not approved
automatically. Reapprove your own link or reopen an incompatible preview as instructed.
Uninstall retains plugin state and does not delete upstream requests or media. No automatic
reset/restore, database downgrade or uncertain external-write replay is performed.

Use [recovery safeguards](../plugin/README.md#persistence-and-recovery-safeguards),
[safety boundaries](../plugin/SAFETY.md), [security findings](../plugin/SECURITY_REVIEW.md),
[feature status](../plugin/FEATURES.md) and [acceptance evidence](../plugin/ACCEPTANCE.md).
