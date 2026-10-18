# Media Hub connection settings

Open **Dashboard → Plugins → Media Hub → settings** on latest stable Jellyfin (currently 12.2.0).

The **Settings section** dropdown groups Jellyfin/Seerr, Discord, Activity, music, books, messaging,
removal and approved servers. **Help** buttons and expandable connection details explain fields in
place. There is no step-by-step wizard or progress state.

- **Save server settings** saves all sections explicitly, with a list of changed fields.
- Section/help changes do not save, connect, send messages or enable services.
- Empty password fields keep saved secrets; explicit removal requires confirmation.
- Reload asks before discarding drafts. Page/account/access changes clear private drafts.
- Service-folder/profile dropdowns load from saved connections only; discovery never auto-approves servers.
- Personal preferences, account linking and messaging-operator authority remain separate from server setup.

See the [field reference](../plugin/ONBOARDING.md), [installation](setup.md) and
[feature/acceptance checklist](../plugin/FEATURES.md).
