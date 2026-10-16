# OAuth linking and optional Activity preview

**New installation?** The integrated **Set up Media Hub** tutorial opens in the native plugin
settings. Its Activity step includes the real OAuth/Activity fields and explains the two
different secrets, both redirects, permissions, directory mapping,
Discord's App Launcher/Launch entry point, and the first account link. No `/activity` slash command
is registered by the native plugin. Pause/resume/restart it at any time without resetting saved
settings. The [onboarding reference](ONBOARDING.md) is optional additional detail.

These transports have automated safety tests, not completed live Discord/browser acceptance.
Use a separate **test** Discord application. Do not enable the production bot or change existing
Jellyfin networking/DNS to try them. No extra listener, runtime service or CDN is required.

## Link setup

1. Configure the existing public HTTPS Jellyfin URL, including its base path, in Media Hub's
   administrator settings. Configure Seerr and its verified Jellyfin user mappings.
2. In the same test Discord application's OAuth settings, register the exact callback:
   `https://jellyfin.example/base/MediaHub/Link/OAuth/Callback` (replace host/base).
3. Save the application's ID and client secret in Media Hub's integration settings. Secrets
   are encrypted server-side and never returned. PKCE can be disabled only when the provider
   does not support it; state, native browser proof and the callback cookie remain mandatory.
4. Request your own `/hub-link` or `/link` in Discord. Open the private link, sign into
   Jellyfin if necessary, verify Discord in that browser, then explicitly confirm the two
   displayed accounts. Never approve another person's forwarded link/code.

The callback cookie is Secure, HttpOnly, SameSite=Lax and scoped to the callback under the
configured base path. Reverse proxies must preserve the correct HTTPS scheme/host through
Jellyfin's supported trusted-proxy configuration; the plugin does not relax this check.
Callback GET cannot link an account. Only an opaque, expiring handoff is retained through
login; native/Discord credentials never go in handoff URLs, page markup or browser storage.
Ordinary-user/login-resume navigation needs compatible Plugin Pages and File Transformation.
Explicit code approval remains the fallback for unsupported clients.
Retained OAuth proofs now authenticate their actor, browser/cookie, expiry and provider result.
Old incompatible proofs must be restarted, never edited or promoted to a completed state.
Review-only unsigned integration settings must first be explicitly saved by a native
administrator; see [SECURITY_REVIEW.md](SECURITY_REVIEW.md) for upgrade limits.

## Activity setup

1. Build the bundle before building the plugin ZIP:

   ```sh
   npm ci --ignore-scripts --prefix plugin/activity
   npm run build --prefix plugin/activity
   ```

2. In the test application's Activities URL mappings, map prefix `/` to the directory
   `jellyfin.example/base/MediaHub/Activity/`. Do not include a protocol or map to an HTML file.
   The directory serves the app; its relative `Config`, `Api/…` and `Assets/…` routes stay
   inside that mapping. A direct development override may use
   `https://jellyfin.example/base/MediaHub/Activity/App`.
3. Configure allowed guild IDs, enable the test application's supported platforms/Activity,
   and explicitly enable Activity in Media Hub. Launch inside an allowed Discord server.
   OAuth requests `identify` and `guilds`; server-side checks independently verify the
   application audience, human user, scopes and current guild membership.

Register both the exact native callback above and the SDK's documented `https://127.0.0.1`
placeholder in **OAuth2 → Redirects**; the latter is not a public hosting address or a new listener.
Enable **Activities → Settings**, then test as the owner/trusted developer-team member with
Developer Mode on. In an allowed **server**, open Discord's **App Launcher** (or voice Activities
button), select the app and **Launch**. Keep Discord's managed Launch entry-point behavior.
Human channel permissions include **Use Activities / Use Embedded Activities** and **Use
Application Commands**. Do not add ordinary users to the developer team to bypass distribution
requirements; bot installation is not Activity publication. DM/group-DM Activity contexts are
not supported by this adapter. An ordinary browser visit to `/Activity/App` cannot test the SDK
authentication flow.

Activity sessions are memory-only, expire within 15 minutes, and never expose a Jellyfin
bearer. Revoked native accounts/tokens, configuration changes and Discord account switches
block private operations. Shared services enforce library visibility and separate operator
messaging authorization. Host configuration and synthetic/native-player SyncPlay operations
are not exposed by the Activity adapter.

For appearance, open the native Media Hub personal preferences and choose **Use this host's
colors in Activity**. Only four validated RGB colors are saved for that user. Arbitrary CSS,
fonts, image URLs and credentials are not transferred; arbitrary Jellyfin themes are not
claimed to reproduce exactly in the cross-origin Activity.

Live proxy, OAuth, companion-plugin, mobile and multi-browser acceptance remains a release
gate. No public application, catalog or production service was activated by adding this code.

Discord reference: [URL mapping](https://docs.discord.com/developers/activities/development-guides/local-development#url-mapping)
and [network security](https://docs.discord.com/developers/activities/development-guides/networking#security-considerations).
