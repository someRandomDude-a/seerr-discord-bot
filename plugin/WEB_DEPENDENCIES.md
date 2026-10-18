# Native Web dependencies

Install **File Transformation** and **Plugin Pages** from their [upstream repository](https://www.iamparadox.dev/jellyfin/plugins/manifest.json), choosing builds for **Jellyfin 12.2.0**, then restart. Install File Transformation first. Media Hub does not install, bundle, configure or edit these plugins or Jellyfin Web files.

## Compatibility, not exact runtime pins

- File Transformation: upstream Plugin Pages documents **at least 2.2.1.0**.
- Plugin Pages: Media Hub uses the documented **3.0.0.0+** registration API, including cleanup and base-path handling.
- Both must expose their public static registration and removal methods. Discovery crosses assembly load contexts; JSON payloads are constructed in the receiving plugin's context when needed. Missing/unsupported APIs leave the Web integration unavailable and report dependency status; Jellyfin continues running.
- The bootstrap joins the same exact `index.html` transform pipeline as Plugin Pages/Home Screen Sections. A separate regex pipeline can be shadowed by the provider's exact-match precedence and must not be used for this shared resource.
- Plugin Pages resolves a leading-slash `pageUrl` with `ApiClient.getUrl` itself. Nested links use `/web/ConfigurationPage?name=mediahub`, **without duplicating the server base path**; the outer native URL still includes that base path. Non-root runtime acceptance is distinct from string-only URL tests.
- `repository/web-dependencies.json` pins **3.0.2.0 / Jellyfin 12.2.0** archives and hashes for reproducible disposable-fixture and CI tests only. It is not an exact-version restriction on users. Future versions with the required API are not rejected solely because their version changed; they still need the correct Jellyfin ABI and acceptance testing.
- Status reports registration, not a claim of client support. Different Web clients, skins and base paths still require acceptance.

## Upstream guidance used

- [File Transformation README](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation#referencing-this-as-a-library): reflection across `AssemblyLoadContext.All`; in-process callbacks instead of editing files.
- [Plugin Pages README](https://github.com/IAmParadox27/jellyfin-plugin-pages#the-plugin-way-3000): v3 page payloads, `RegisterPage` and `RemovePage`; minimum File Transformation version and ABI-specific catalogue builds.
- [Home Screen Sections integration](https://github.com/IAmParadox27/jellyfin-plugin-home-sections/blob/288a1f6ae7a9af3e4ac0d145b6285d84888d0ab1/src/Jellyfin.Plugin.HomeScreenSections/Services/StartupService.cs): reflection discovery, missing-plugin warnings, and feature-specific minimum-version checks rather than exact pins. Media Hub intentionally does **not** copy its legacy configuration-file-writing fallback.

## Acceptance

`tools/Test-WebDependencies.ps1` installs checksum-verified upstream archives only into the label-verified owned Docker fixture. It refuses unreviewed replacements. The release smoke installs both dependencies before native browser tests; no manual script loading is allowed. Production remains unchanged.
