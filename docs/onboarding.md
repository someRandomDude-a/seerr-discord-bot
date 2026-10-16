# Integrated Media Hub setup tutorial

You do not need to prepare configuration files or read a separate manual before setup.

1. Install Media Hub on a test Jellyfin 12 host and restart Jellyfin.
2. Sign in as the Jellyfin administrator and open **Dashboard → Plugins → Media Hub → settings**.
3. A fresh configuration automatically starts **Set up Media Hub**. Follow its steps; the actual
   settings forms appear inside the relevant step.

The tutorial covers Discord application creation and installation, limited permissions, copying
the correct IDs/secrets, Seerr account mapping, optional messaging intents, OAuth/Activity directory
mapping and launching, account linking and safe first reads. Optional integrations can stay off.
No step requires a broadcast, media request or deletion as a connectivity test.

## Tutorial controls

- **Back / Next:** navigate explanations; neither saves settings nor enables an integration.
- **Save plugin settings / Save integrations:** explicitly save the displayed configuration form.
  Saving validates/persists settings; it does not prove a live connection test passed.
- **Pause tutorial:** return to the regular page without resetting settings or current drafts.
- **Resume setup tutorial:** continue the unfinished walkthrough.
- **Restart tutorial:** replay from the first step at any time, without clearing settings,
  credentials or links. Drafts remain available for review.
- **Finish tutorial:** remember walkthrough completion, not production approval or successful
  external-service verification. Unsaved settings block forward configuration/finish actions.

Progress is browser-local, scoped to the native Jellyfin user/server/page, and contains only the
tutorial version, step and completion flag. Credentials and form drafts are never persisted in
tutorial storage. Clearing browser storage or changing browsers may require starting again;
the tutorial still works when storage is unavailable. Server configuration remains unchanged.

## Access and first account link

Integration setup requires a native Jellyfin administrator, not an Activity session or API key.
Ordinary users open **Media Hub** from Jellyfin's main menu after compatible Plugin Pages and
File Transformation are available. They request their own `/hub-link` in an allowed Discord
server, sign in as their own Jellyfin user and explicitly approve the displayed identities.
Do not promote viewers or approve their links using another account.

Use the [onboarding reference](../plugin/ONBOARDING.md) for additional field-by-field detail,
[setup guide](setup.md) for installation, and [Activity reference](../plugin/ACTIVITY.md) for
optional Discord embedding. This remains a test preview; complete the documented acceptance
and security gates before production use.
