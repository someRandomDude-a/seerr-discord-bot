# Media Hub for Jellyfin 12

Media Hub is an **in-process .NET 10 plugin** for Jellyfin 12. Discord commands, media services,
background work, configuration and the optional Discord web Activity run through Jellyfin.
No separate application server, admin listener or runtime service is installed.

> **Preview, not production-ready.** Use a test Jellyfin and test Discord application.
> Automatic deletion defaults off. SyncPlay invitations remain blocked without exact
> creator-session proof. Provider/client acceptance and several features are still incomplete.
> See the [feature checklist](plugin/FEATURES.md) and [security review](plugin/SECURITY_REVIEW.md).

## Install and set up

1. Use an installable, version-matched plugin ZIP on Jellyfin **12.x**. Source files alone do not
   install a plugin. The package references Jellyfin API **12.0.0** and is also tested against **12.2.0**.
2. When the catalog has been published, add its HTTPS manifest under
   **Dashboard → Plugins → Repositories**:

   ```text
   https://somerandomdude-a.github.io/seerr-discord-bot/manifest.json
   ```

   **This URL is the publication target, not a claim that the catalog is live.** The owner must
   run the [release workflow](plugin/README.md#publish-the-repository), then verify the manifest,
   downloadable ZIP and catalog installation. Do not add the Git clone URL as a repository.
3. Install **Media Hub (Preview)** and restart Jellyfin. For ordinary-user navigation and login
   resume, install compatible Jellyfin-12 **Plugin Pages** and **File Transformation** versions.
4. Sign in as the Jellyfin administrator, open **Dashboard → Plugins → Media Hub → settings**,
   and follow **Set up Media Hub**. It opens automatically for a new configuration.

The integrated tutorial walks through Discord app creation, least-privilege permissions,
IDs/secrets, the actual configuration forms, account linking and optional Activity setup.
Use **Back / Next / Pause / Resume / Restart tutorial** at any time. Restart does not reset
settings or links. Only explicit Save actions change configuration; tutorial progress stores no
credentials or drafts. The [onboarding reference](plugin/ONBOARDING.md) is optional extra detail.

## Features

- Native Jellyfin account/session verification, current library visibility, separate administrator
  integration settings and personal preferences; no administrator impersonation of viewers.
- User-scoped Seerr discovery, movie/TV request previews and confirmation, permissions/quotas,
  request history and two-way watchlist membership. Seerr remains authoritative for those requests.
- Accessible movie/TV/music/book libraries, selected Lidarr/Readarr requests, service/storage
  projections, personal subscriptions, status feed and opt-in Discord notifications.
- Discord media commands and separately authorized operator inbox, messages, attachments and
  previewed announcements. Ambiguous requests/messages are not automatically resent.
- Anonymous shared deletion queue, owner-only Undo, current recent-viewer consent and a full
  delay. Removal is through verified Arr file APIs only; no direct media filesystem deletion.
- Bundled optional Discord web Activity using the same application services and permission checks.
  It cannot configure the host or impersonate a native player. Launch it through Discord's App Launcher.

This list describes implemented preview functionality, not completed live-provider acceptance.
See [FEATURES.md](plugin/FEATURES.md) for supported paths and remaining work.

## Safety and state

The plugin owns only **`<Jellyfin data>/media-hub/`**. It does not directly open/migrate Jellyfin's
databases, persist web-file edits or change host/network settings. Uninstall retains plugin
state. Corrupt, foreign, incompatible or unverifiable state is not silently reset or approved.
Back up the full plugin directory consistently with its matching encryption key.

Configuration, user data and external responses are untrusted. Integration settings are
authenticated; discovered Arr destinations additionally require independent administrator
approval. In-process resource/provider/filesystem risks and unresolved release blockers remain;
unit tests cannot establish absolute host immunity or production safety.

## Develop and release

Requires **.NET 10 SDK** and **Node 22.12+** for development only. Run from the repository root:

```sh
dotnet test plugin/tests/MediaHub.Tests/MediaHub.Tests.csproj -c Release -p:JellyfinVersion=12.0.0
dotnet test plugin/tests/MediaHub.Tests/MediaHub.Tests.csproj -c Release -p:JellyfinVersion=12.2.0
npm ci --prefix plugin/web
npm test --prefix plugin/web
npm ci --ignore-scripts --prefix plugin/activity
npm run build --prefix plugin/activity
```

Code and resources live in `plugin/src/`; native tests in `plugin/tests/`; browser tests in
`plugin/web/`; the bundled Activity source in `plugin/activity/`; packaging and guarded disposable
Jellyfin acceptance tools in `plugin/tools/`. CI tests these plugin components; release tooling
builds an allowlisted ZIP, checksums and `manifest.json`, then publishes immutable preview assets
and the catalog only when explicitly run.

### Documentation

- [Setup](docs/setup.md) · [Tutorial controls](docs/onboarding.md) · [Detailed plugin reference](plugin/README.md)
- [Activity configuration](plugin/ACTIVITY.md) · [Implementation plan](plugin/IMPLEMENTATION_PLAN.md)
- [Feature checklist](plugin/FEATURES.md) · [Safety boundaries](plugin/SAFETY.md)
- [Security findings](plugin/SECURITY_REVIEW.md) · [Acceptance evidence](plugin/ACCEPTANCE.md)
- [Packaging and publishing](plugin/README.md#build-and-test) · [Release notes](plugin/repository/RELEASE_NOTES.md)

MIT licensed. See [LICENSE](LICENSE) and [third-party notices](plugin/THIRD_PARTY_NOTICES.md).
