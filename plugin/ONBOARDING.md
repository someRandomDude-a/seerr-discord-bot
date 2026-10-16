# Start here: set up Media Hub, Discord and the Activity

**Setup is built into the plugin.** On a fresh installation, open Media Hub's settings as the
Jellyfin administrator: the **Set up Media Hub** tutorial starts automatically. It walks through
one step at a time and places the actual configuration forms inside the relevant steps.
You do not need to read this document or write code to set up the plugin; this page is a reference.

Use **Back / Next**, **Pause tutorial**, **Resume setup tutorial**, or **Restart tutorial** at any
time. Restart replays the explanation; it does **not** reset settings, links, tokens or form drafts.
Only **Save plugin settings** and **Save integrations** save configuration. Next/Finish never enable
features, send messages, create requests or claim a connection test passed. Unsaved settings must
be explicitly saved before moving forward from their configuration step or finishing. Back and
Pause keep the draft available for review.

Progress remembers only the tutorial version, step and completion flag in this browser, scoped
to the current Jellyfin user/server/page. No credentials or form values are stored with it. It
resumes after returning to the page; another browser may need **Start setup tutorial**. If browser
storage is unavailable, the tutorial still works but cannot remember progress across visits.
Each **Check before continuing** explains a manual check; finishing is not production approval.

> **Test preview only.** Media Hub is not production-ready or feature-complete. Use a disposable
> Jellyfin and a new test Discord application/server. Real Discord/Seerr/Arr/Activity acceptance
> is still required. Leave automatic deletion off. This guide does not enable or publish anything
> for you. See [known limitations](SECURITY_REVIEW.md).

**Already joining someone else's Media Hub?** Ask the server owner to finish setup, then go
straight to [8 · Link your own account](#8--link-your-own-account-and-make-a-safe-first-read). You do not need a bot token,
client secret, API key or Discord Developer Portal access.

## What you are setting up

| Term | Plain-language meaning |
| --- | --- |
| Discord application / app | The project's entry in Discord's Developer Portal. It holds the bot and optional Activity settings. |
| Bot | The app's account in your Discord server. It answers commands such as `/dashboard`. Jellyfin runs its connection. |
| Server / guild | Your Discord community, not your Jellyfin computer. Discord's API calls servers “guilds.” |
| Slash command | Type `/` in a Discord server's message box, then choose a command belonging to your app. |
| Activity | The web interface embedded inside Discord. It is optional; it is not a video player or a second Jellyfin server. |
| OAuth | Discord's consent/sign-in screen that proves which Discord account you own. It is separate from installing the bot. |
| Intent | Which kinds of events Discord sends the bot. It is **not** a server-role permission. |

There are three separate permission systems: **Discord** controls channels/commands,
**Jellyfin** controls accounts/libraries and who can change plugin settings, and **Seerr** controls
movie/TV requests/quotas. Passing one check does not bypass the others.

## 1 · Gather what you need and open the right page

You need:

- A test Jellyfin **12.x** with the **Media Hub (Preview)** plugin installed and Jellyfin restarted.
  Source files alone are not an installed plugin. If you do not have an installable preview ZIP,
  ask the maintainer; the catalog is not automatically published by this repository. See
  [installation/build instructions](README.md#install-from-the-plugin-repository).
- A Jellyfin **administrator** account for setup, plus an ordinary test account for checking access.
- A Discord account and a test Discord server you own or have **Manage Server** permission in.
- Your existing user-facing **HTTPS Jellyfin base URL**, such as `https://jellyfin.example` or
  `https://media.example/jellyfin`. Replace all `.example` addresses in this guide with your own.
- For movie/TV discovery/requests: an already configured Seerr, its administrator API key,
  and Jellyfin users imported/mapped in Seerr.

Open Jellyfin in a browser and sign in as the administrator. Open **Dashboard → Plugins**,
find **Media Hub (Preview)** under installed plugins (sometimes called **My Plugins**), and
open its settings/configuration page. If your Jellyfin version shows a menu on the plugin card,
use that menu's settings option.

At the top you will see **Media Hub** and **Set up Media Hub**. A new configuration automatically
opens the first tutorial step. On an existing installation use **Start setup tutorial** or
**Restart tutorial**. The configuration steps contain the same working forms as the main page;
pausing returns them to their usual locations without discarding drafts. On the main page:

- **Link Discord** is for each person's account; it does not configure the bot.
- **Your preferences** is personal, including notification opt-in. It is not server configuration.
- **Plugin settings · Jellyfin administrators only** contains the bot/server settings.
- **Music, books and messaging** contains optional integrations and OAuth/Activity settings.

There are **two separate Save buttons**: **Save plugin settings** for the first configuration
form, and **Save integrations** for the second. Finish and save one form before editing the other.
Changing plugin settings reconnects the bot; it does not normally restart Jellyfin. Installing or
upgrading the plugin itself does need a host restart. A “saved” notice validates/persists settings;
it is **not** proof that Discord or a media service works.

Ordinary users need compatible Jellyfin-12 **Plugin Pages** and **File Transformation** for the
user-facing page/login-resume navigation. The standard Dashboard configuration page is
administrator-only; do not promote viewers just to let them link. Ask the Jellyfin administrator
to install compatible companion versions if the user page is missing.
With those companions available, open Jellyfin's main navigation menu (usually the three-line
button at top left) and choose **Media Hub**. If that entry is absent, stop and ask the owner;
do not try to use their Dashboard URL or administrator login.

The plugin runs entirely inside Jellyfin and uses its existing authenticated settings page and
listener. Use one plugin instance per application token. No new admin port, VPN/DNS change or
network reassignment is required. The plugin does not create HTTPS hosting for you: if you do not already
have a suitable HTTPS URL, ask the server administrator before exposing anything.

**Check before continuing:** the administrator can see plugin settings. Automatic deletion and
Discord integration are off. The ordinary account cannot see the administrator form.

## 2 · Create a new Discord application and get its bot token

1. Open [Discord Developer Portal](https://discord.com/developers/applications) in another browser
   tab. Sign in with **your Discord account**, not your Jellyfin account.
2. Click **New Application**, give it a recognisable test name such as **Media Hub Test**, accept
   Discord's terms if prompted, and click **Create**. Keep this tab open.
3. The left sidebar belongs to this application. Click **General Information** and copy the
   **Application ID** into a private setup note. It is a long number, not the app name.
4. Click **Bot** in that sidebar. New apps normally already have a bot user; if Discord offers
   **Add Bot**, use it. You may set a name/icon, but those do not grant permissions.
5. Under **Token**, use **Reset Token** if needed, complete Discord's password/MFA prompt,
   and copy the new token into a password manager. Resetting a token invalidates the old one.
6. Leave **Requires OAuth2 Code Grant** off for the normal bot installation flow. Leave
   **Presence**, **Server Members**, and **Message Content** privileged intents off for now.
   We will enable the last two only if you choose the features that need them in step 7.
7. Leave **Interactions Endpoint URL** blank. Media Hub receives commands through an outbound
   Discord Gateway connection; the webhook/Express endpoint in generic bot tutorials is not used.

### Do not mix up these values

| Value | Where to find it | Where it belongs | Secret? |
| --- | --- | --- | --- |
| Bot token | Developer Portal → Bot → Token | Media Hub **Discord bot token** | **Yes** — controls the bot |
| Application ID / Client ID | General Information / OAuth2 | **Discord application ID** in step 9 | No — identifies the app |
| OAuth client secret | OAuth2 → Client Secret | **OAuth client secret** in step 9 | **Yes** — not the bot token |
| Server ID | Discord client → Copy Server ID | **Allowed Discord server IDs** | Not a password |
| Your user ID | Discord client → Copy User ID | Linking / optionally **Operator Discord IDs** | Not a password |
| Seerr API key | Seerr administrator settings | **Seerr key** | **Yes** |

The app's **Public Key** is not any of these secrets and is not requested by this plugin.
Never paste a secret into a Discord message, a URL/mapping, a screenshot, a support issue, or a
frontend source file. If one leaks, rotate that specific secret at its provider and update the
native plugin's password field. Do not delete the plugin database/key as a troubleshooting step.

**Check before continuing:** you can identify the application name, its Application ID, and
its separate bot token. No token has been sent to a Discord channel.

## 3 · Install the bot into your test Discord server

1. In the application's Developer Portal sidebar, open **Installation**.
2. Under **Installation Contexts**, enable **Guild Install** (guild means server). A **User Install**
   alone is not enough: this plugin registers commands in allowed servers and rejects commands
   from ordinary DMs. You do not need to enable user installs for this setup.
3. Under **Install Link**, choose **Discord Provided Link** if available. Under **Default Install
   Settings → Guild Install**, select the scopes **`bot`** and **`applications.commands`**.
   Scopes tell Discord to install the bot account and its slash commands; they are not API keys.
4. Choose the bot permissions below. Do **not** choose Administrator “to make everything work.”
5. Save changes. Copy the **Install Link**, open it in a new tab, choose **Add to Server**, select
   your test server, and click **Continue → Authorize**. Complete Discord's confirmation if prompted.
   If no server is available, sign into the correct Discord account or ask its owner to install it.
6. Alternative if your Portal layout uses it: **OAuth2 → URL Generator**, select the same two scopes
   and permissions, then open the generated URL. Never paste the bot token into that URL.

### Bot permissions: grant only what you will use

| Permission on the bot's role/channel | Why / when |
| --- | --- |
| **View Channels** | Let the bot see the test channel. |
| **Send Messages** | Channel messages and operator replies. |
| **Embed Links** | Rich command responses and notification cards. |
| **Read Message History** | Messenger conversation/history access checks. It does not import all old messages. |
| **Attach Files** | Only if you want messenger uploads/attachments. |
| **Send Messages in Threads** | Only if you want to use existing threads. Start with a normal text channel. |

Do not grant **Administrator, Manage Server, Manage Roles, Manage Channels, Manage Messages,
Manage Webhooks**, or mass-mention permissions. **Connect/Speak** are not needed: Media Hub does
not join voice as a streaming bot. The person *installing* the bot needs Manage Server; the bot
itself does not.

### Check the actual channel, not just the invite

Create a text channel such as **#media-hub-test**. Right-click it → **Edit Channel → Permissions**.
Select/add the bot's role and ensure the permissions above are allowed there. A category/channel
override can deny access even when the bot role's server-wide setting allows it. Check the
category too if the channel inherits its permissions. Do not move the bot role above everyone
or give it Administrator to bypass an override.

For the **people** using the bot, allow **View Channel**, **Send Messages** for the initial text
test, and **Use Application Commands**. Under **Server Settings → Integrations → your app**,
also check any command restrictions on roles/channels. For Activity later, allow **Use Activities**
(sometimes labelled **Use Embedded Activities**); voice-channel testing also needs normal human
voice-channel access. These are the members' permissions, not extra bot privileges.

**Check before continuing:** the bot appears in the server member list. It may still be offline;
that is expected until you enable the connection in Jellyfin in step 6.

## 4 · Copy your server ID and your own user ID

In the **Discord client** (not Developer Portal):

1. Click the gear beside your username → **User Settings → Advanced → Developer Mode** → on.
   This only exposes useful copy-ID options; it does not turn you into a programmer or grant access.
2. Close Settings. Right-click your test **server icon** in the leftmost column → **Copy Server ID**.
3. Right-click **your own user/avatar** → **Copy User ID**.
4. If configuring the inbox later, right-click **#media-hub-test** → **Copy Channel ID**.

Keep these numbers labelled so you do not interchange them. A server name, invite link,
channel name, Discord username and Application ID are not a Server ID.

**Check before continuing:** you have a Server ID for the exact server the bot joined.

## 5 · Fill the native Jellyfin configuration, still disabled

Return to Jellyfin's **Plugin settings · Jellyfin administrators only** form.

| Field | What to enter |
| --- | --- |
| **Enable automatic deletion through Radarr/Sonarr** | **Leave unchecked.** Do not test deletion as part of onboarding. |
| **Recent activity window / Default deletion delay** | Leave the defaults **14 days / 2 days**. |
| **Enable Discord integration** | Leave unchecked for this save. |
| **Public Jellyfin URL** | Your existing user-facing HTTPS **base** URL. Example: `https://media.example/jellyfin`. No `/web`, `/index.html`, login URL, query, or token. Include a real base path only if your host uses one. |
| **Allowed Discord server IDs, comma separated** | The Server ID from step 4. Add another only after intentionally installing/authorizing the bot there. **Blank does not mean allow everyone**; enabling Discord requires an explicit list. |
| **Discord bot token** | The token from **Bot → Token**, not the Application ID or client secret. |
| **Seerr URL / Seerr key** | Seerr's reachable base URL and administrator API key (see below). |

Click **Save plugin settings**, wait for **Settings saved**, then reopen the page if requested.
Saved password fields clear intentionally; **blank keeps the saved secret**, rather than showing
it back to you. Do not repeatedly regenerate a valid token just because the field is empty.

### Prepare Seerr without changing existing requests

1. Sign into **Seerr** as its administrator. Confirm its existing Jellyfin connection and existing
   Radarr/Sonarr services work. You do not need to recreate requests or change service routing.
2. Find the administrator **API Key**, normally under **Settings → General**, and copy it only into
   **Seerr key**. Menu wording can vary with Seerr releases.
3. Enter a Seerr base address reachable **from Jellyfin's process/container**, such as
   `http://seerr:5055` only when that name already resolves on its existing network. Do not append
   `/api/v1`. Inside a container, `localhost` refers to that container, not another service.
   Do not change working VPN/DNS/network assignments to match an example.
4. In Seerr's **Users** administration, import the relevant Jellyfin users using its supported
   import/account workflow. Each viewer must map to exactly their native Jellyfin account.
   Similar names/email addresses do not establish a link; duplicate mappings must be resolved.
5. Set request permissions, approval rules and quotas **in Seerr** for an ordinary test user.
   The plugin's server-side API key does not make that viewer an administrator.

**Check before continuing:** the settings save succeeds, deletion remains off, and the public
Jellyfin URL opens your own host in another browser tab. Service connectivity and user mapping
will be checked by the actual reads in step 8; a successful save alone does not test them.

## 6 · Enable Discord and check the first command

1. Confirm only this plugin instance uses the test application token.
2. In the first Jellyfin configuration form, tick **Enable Discord integration** and click
   **Save plugin settings**. Keep automatic deletion unchecked.
3. Give the bot a short time to connect. In **#media-hub-test**, type `/`, select **your test app**,
   and choose **`/hub-link`**. A private response should show your Discord ID and a short-lived code.
4. If commands do not appear, use the troubleshooting table below before changing permissions.

You do not run a separate command-registration script. The plugin registers commands for its
configured allowed servers when its Gateway connection becomes ready.

**Check before continuing:** `/hub-link` answers privately in the allowed server. This proves
the bot connection/command path, **not** your Seerr mapping or permission to view media.

## 7 · Choose optional messaging permissions carefully

In **Music, books and messaging**, you may leave all these options at their defaults and proceed
to linking. Do not enable privileged intents just for slash commands or Activity authentication.

| Feature | Developer Portal → Bot → Privileged Gateway Intents | Matching Media Hub setting |
| --- | --- | --- |
| Ordinary slash commands and buttons | None | Leave both intent boxes off |
| Retain ordinary non-mention server-message content in the operator inbox | **Message Content Intent** on | **Message Content Intent** on |
| Announcement option to DM all human server members | **Server Members Intent** on | **Members Intent** on |
| Online/presence tracking | Not used | **Presence Intent stays off** |

An intent must be enabled in **both** places before that feature is used. Larger/verified apps
may need Discord approval; do not attempt to bypass that requirement. Direct messages and
messages mentioning the bot have Discord's content exceptions; receiving an event does not
give access to every channel or old chat history.

- **Operator Discord IDs:** start empty, or add only your own copied **user ID** if testing the
  messenger. An operator can read the bot's shared private DM inbox, not just their own DMs.
  Explicit operators still need a live linked Jellyfin identity. Giving someone a Jellyfin admin
  role, Discord moderator role or a matching display name does **not** grant operator access.
- **Also allow verified Seerr administrators…:** optional; leave off unless you intentionally
  want every qualifying, linked Seerr administrator to have messaging-operator access.
- **Retained inbox channel IDs:** explicitly choose channels whose eligible incoming messages
  should be retained. Retention is not a complete Discord history import. Inform members of
  your retention/privacy policy. Start with the test channel, **7 days**, and a small recipient
  limit; attachments also contain private data in backups.
- Keep **Also DM all human members…** off when sending your first test. Use a single expected
  recipient, never a mass broadcast to test permissions. Blocked DMs can be a recipient's privacy
  setting; do not circumvent it.

Click **Save integrations** after changing this second form. Check the first form's settings
are already saved; unsaved edits in the other form are not applied by this button.

## 8 · Link your own account and make a safe first read

### First visit without OAuth configured (code approval)

1. In the allowed Discord server run **`/hub-link`** (or **`/link`**).
2. Keep the private response open. Copy its **fallback code** and **your Discord user ID**.
   The code expires after five minutes; request a fresh one if needed.
3. In your own browser, sign into **your own Jellyfin user account**, open the main navigation
   menu → **Media Hub**, and find **Link Discord**. An administrator can use the Dashboard page
   for their own account, but must not approve viewers' codes while signed in as the administrator.
4. Paste the code and the exact Discord ID displayed to **you**. Click **Link account**.
   Approve only a code you requested yourself. Never post or forward it for another person to approve.
5. Back in Discord, run **`/dashboard`**, then **`/library`** to check accessible native media.
   Try **`/discover`** or **Your requests** to test the viewer's Seerr mapping/permissions.
   Empty test libraries can legitimately show no results.

### After optional OAuth is configured

The private response also offers **Link in Jellyfin**. Open it, sign into your own Jellyfin
account, choose **Verify Discord in this browser**, and check Discord is the same person who
requested the link. On return, review both identities and click **Confirm these accounts**.
The callback/opening a link alone never approves the account. Start again if the proof expires,
you change accounts/devices, or the administrator changes OAuth settings.

### Know what you are looking at

- **Media:** browse **Your library**, **Discover**, or **Your requests**. Opening **Details** is
  an explicit interaction; it may update last-played time, not completion/progress/favorites.
  Passive lists/polls do not count as playback.
- **Prepare request** is a preview; **Confirm request** actually sends it. Skip that button
  during a read-only setup test. A timeout/uncertain receipt is not an invitation to resend.
- **Your preferences:** choose whether to allow personal Discord DMs, then **Save preferences**.
  DMs are opt-in and **Mute notifications** wins over other choices. Messenger operators have
  separate intentional messaging powers; the personal preference is not a global inbox policy.
- **Operator messenger** appears only for an authorized operator. If testing it, send one clearly
  labelled test message to **#media-hub-test**, confirm the expected destination, and check its receipt.
- **Watch together:** invitations are currently blocked without exact creator-session proof.
  Do not weaken permissions or impersonate a player to make them work. Native Jellyfin SyncPlay
  remains separate; Activity is not a replacement player.

**Check before continuing:** an ordinary linked user can see only their permitted media, cannot
open integration settings, and cannot open the messenger unless explicitly authorized. This is
more meaningful than testing everything while signed in as an administrator.

## 9 · Add OAuth and the optional Discord web Activity

You can stop at step 8 and use native pages/slash commands. For Activity, use the **same test
application** as the bot; do not mix an ID from one app with a secret from another.

### A. Check hosting before configuring Discord

The Activity frontend and its API are **bundled in the plugin** and served by the existing
Jellyfin listener. End users do not install another runtime, build a web app, open a second port,
or run another service. Source maintainers must build the Activity **before packaging**; a missing
bundle needs a corrected plugin package, not a frontend development server on the user's host.

Your existing HTTPS hostname must be reachable by Discord's proxy, not only inside your LAN/VPN.
A trusted certificate and correct existing Jellyfin base-path/reverse-proxy routing are required.
An additional browser/SSO challenge that only you can satisfy may prevent the proxy loading it.
Ask the hosting administrator to evaluate secure routing; **do not disable authentication,
certificate checks, frame policies or expose Arr/admin services** to make it load. A host URL
reachable in your browser is a useful first check, not proof of proxy/SDK compatibility.

### B. Configure OAuth in Developer Portal

1. Select your test application → **OAuth2**. Copy its **Client ID/Application ID** and
   **Client Secret** into a password manager. The client secret is separate from the bot token.
2. Under **Redirects**, add the exact native linking callback and **Save Changes**:

   | Your configured Public Jellyfin URL | Native linking redirect to add |
   | --- | --- |
   | `https://jellyfin.example` | `https://jellyfin.example/MediaHub/Link/OAuth/Callback` |
   | `https://media.example/jellyfin` | `https://media.example/jellyfin/MediaHub/Link/OAuth/Callback` |

   Use your real hostname/path, exact spelling/case, HTTPS and no extra trailing slash.
   This URL is **not** the Activity URL mapping or an Interactions Endpoint URL.
3. Also add **`https://127.0.0.1`** as the Embedded App SDK's documented placeholder redirect
   for Activity authorization. Keep the native callback too. This placeholder is not a hosting
   address and does not require exposing or running a service on your loopback interface.

### C. Enable Activity and map the right directory

1. In Developer Portal → **Activities → Settings**, enable **Enable Activities** and select
   the **Supported Platforms** you are actually testing (start with web/desktop).
2. Under **Activities → URL Mappings**, add **Prefix `/`** and the directory target below.
   In the **Target** field omit `https://`; **do not** point to an HTML file, `/App`, Jellyfin's
   `/web` interface, a private IP, Seerr, or another service.

   | Public Jellyfin URL | Prefix | Target |
   | --- | --- | --- |
   | `https://jellyfin.example` | `/` | `jellyfin.example/MediaHub/Activity/` |
   | `https://media.example/jellyfin` | `/` | `media.example/jellyfin/MediaHub/Activity/` |

   Save changes. One root mapping is enough for this app's frontend and relative API/assets;
   do not add overlapping mappings for the same application routes.
3. Enabling Activities normally creates Discord's **Launch** Entry Point command. Keep its
   Discord-managed launch behavior; the plugin does not implement an app-handled custom entry point.
   The current native plugin does **not** register an `/activity` slash command.

### D. Save the matching settings in Jellyfin

In **Music, books and messaging**:

1. Paste the app's ID into **Discord application ID · linking and Activity**.
2. Paste the **Client Secret**, not the bot token, into **OAuth client secret**.
   ID and secret must be configured together. Leave **Use OAuth PKCE when supported** checked;
   if the provider rejects PKCE, ask the maintainer before changing it. State/browser proof
   always remain required; explicit code approval is available without OAuth.
3. Tick **Enable optional Discord Activity** → **Save integrations**. Check the allowed server
   list and public HTTPS URL were already saved in the first form.

### E. Launch as a user and finish linking

1. In Discord as the application owner/trusted developer-team tester, ensure **Developer Mode**
   is on. Enter the **allowed test server**, not a DM/group DM. The app requires a server context.
2. Open the channel's **App Launcher** (or Activities/rocket button in a voice channel), find
   your test app by name, and choose **Launch**. If it is missing, check supported platforms,
   server installation and members' **Use Activities / Use Application Commands** permissions.
3. Approve the requested **`identify`** (your Discord identity) and **`guilds`** (verify your
   allowed-server membership) scopes. These are user OAuth permissions, not privileged Gateway intents.
4. If shown **Link Jellyfin**, click it. Complete the native same-browser account verification
   and explicit confirmation from step 8. Return to Activity and choose **Check link**.
5. Confirm Media Hub's media/preferences controls load. Reopen Activity when verification expires
   or after account/configuration changes; do not copy browser tokens to keep a session alive.

Development Activities may be launchable only by the owner/developer team until Discord's
distribution/release requirements are met. Installing the bot does not publish the Activity.
Do not add ordinary viewers to the app's developer team just to bypass launch restrictions;
that grants sensitive app-management access. Broader distribution is a separate reviewed task.

Opening `…/MediaHub/Activity/App` in a normal browser does **not** prove Activity sign-in: it needs
Discord's Embedded App SDK context. Test the complete launch inside Discord. Activity never gets
a Jellyfin bearer credential, cannot change server integration settings and cannot impersonate
the native Jellyfin player. A **Watch/Open in Jellyfin** action opens the actual native client.

For appearance, in the native page's personal preferences choose **Use this host's colors in
Activity**. Only four validated colors are copied for your account, not arbitrary CSS/fonts or
your whole Jellyfin theme. See [Activity technical details](ACTIVITY.md).

**Check before continuing:** the owner can launch through the real URL mapping, authorize,
link their own Jellyfin user and browse permitted media. An unauthorized server/user must not
gain access. Do not call the Activity tested merely because its HTML or Config URL loads.

## 10 · Optional media services and deletion: leave until the basics work

### Radarr/Sonarr destination approval

Seerr remains authoritative for movie/TV routing, profiles and request history. For plugin
storage inspection or eventual deletion, the administrator must **independently approve** each
discovered service/Seerr-instance/exact address in **Approved Arr destinations**. Discovery is
not permission to contact an arbitrary address. Leave `[]` if you are not ready to approve one.

The field uses JSON (a structured list). For example **only**, not a value to paste unchanged:

```json
[
  { "Service": "radarr", "Instance": 3, "Url": "http://radarr:7878" },
  { "Service": "sonarr", "Instance": 5, "Url": "http://sonarr:8989" }
]
```

`Instance` means **Seerr's numeric service-instance ID**, not its position in a list, port,
quality profile or a movie/series ID. Review the exact scheme/host/port/base path independently
against your owned service and Seerr settings, including separate 4K instances. If Seerr's UI
does not expose its instance ID, ask the administrator/maintainer to identify it through the
supported read-only settings API; do not guess or share a settings response containing API keys.
The current plugin form does not provide an instance-discovery picker. No Arr key is entered here.

### Lidarr / Readarr

Optional music/books use their own **URL/key pair**, existing **root folder**, **quality profile
ID** and **metadata profile ID** from that service's settings. These IDs are not names; a root is
that service's configured destination, not a path you should create/delete inside Jellyfin.
Leave both URL/key blank and request switches off if unused. Do not enable selected-album/book
requests until a compatible test service, profiles, permission limits and edition selection have
been verified. The plugin does not configure these upstream services for you.

### Automatic deletion

**Leave it off in this preview.** A bot with Discord Administrator would not be entitled to
delete media. Only verified original requesters may propose removal; recent viewers need
current unanimous consent (or age out of the default 14-day window), followed by the full
default 2-day delay. New activity invalidates consent. Undo is owner-only while available.
Removal uses exact Arr file APIs, never direct filesystem deletion. Old/unverifiable consent
records are not approved by upgrading. Live provider/concurrency acceptance is still a gate.

## If something does not look right

| What you see | First things to check — without weakening security |
| --- | --- |
| No administrator settings | Sign into native Jellyfin as an administrator, not Activity. Ordinary users must use the companion user page, not get promoted. |
| Bot is offline | Discord integration enabled and saved? Correct **bot token**? Existing outbound Discord connectivity? Check Jellyfin logs for Media Hub's sanitized warning. Do not paste full logs/secrets into chat. |
| Bot is in the server but no commands | Installed with **Guild Install + bot + applications.commands**? Exact Server ID in non-empty allowlist? Correct test app selected in `/` menu? Member/Integrations command restrictions? Reopen Discord after connection is ready. |
| Invalid/disallowed intent / connection fails after changing intents | Match Developer Portal and plugin switches; keep unsupported/unapproved privileged intents off. Presence is never needed. |
| Channel missing, send denied or attachment fails | Check both the linked operator's and bot's actual channel/category overrides; add Attach Files only for files, thread permission only for threads. Private-thread membership is separate. |
| “Link your account” / expired control | Request your own fresh `/hub-link`, approve as the right native user, and reopen the command. Do not edit stored links or reuse another person's proof. |
| Seerr not configured / import or duplicate-mapping error | Reachable base URL/key; native Jellyfin-user import; exactly one mapping; current viewer permissions/quotas. Never substitute an administrator identity. |
| Arr destination not approved | Independently verify service, Seerr instance ID and exact URL. Leave it blocked if uncertain; discovery alone cannot approve it. |
| “Settings changed” | Another save changed the revision. Reload, review the current values, then deliberately reapply the draft. Do not delete state. |
| Older unsigned settings need review | In the elevated native page review every destination/permission/feature, explicitly save and reopen. Integrations block until reviewed. Do not edit the signature/key yourself. |
| OAuth redirect mismatch / browser verification failed | Same app ID/secret? Exact HTTPS callback including base path? Same native browser/device/callback cookie? Trusted-proxy HTTPS scheme correct? Restart with a fresh self-requested link; never turn off cookie/state checks. |
| Activity is missing from App Launcher | Owner/team test access, Developer Mode, Enable Activities, supported platform, allowed guild, install and human Use Activities permission. Bot installation is not Activity publication. |
| Activity is disabled, blank or stuck verifying | Enabled/saved **in both places**? Correct directory mapping (no scheme, no `/App`)? Bundle in the installed ZIP? Public HTTPS/certificate/proxy access? Test inside Discord, not an ordinary browser. No `/activity` command is expected. |
| Activity authenticates Discord but cannot show media | Link the same native account, use Check link, verify current guild membership, native token/account and Seerr mapping. OAuth alone does not authorize media. |
| Request/message outcome “uncertain” / “not confirmed” | Check the upstream request/delivery receipt. Do **not** automatically retry or reset state to pending; the first write may have succeeded. |
| Watch-party invitations blocked | Expected while exact original-player membership cannot be verified. Native SyncPlay remains usable; do not bypass this gate. |

## Finish with this small checklist

- [ ] New test app/server; token used by only this plugin instance; secrets stored privately.
- [ ] Bot installed to the intended server with limited channel permissions, no Administrator.
- [ ] Explicit server allowlist; privileged intents off unless intentionally matched in both places.
- [ ] Both settings forms saved deliberately; automatic deletion still off.
- [ ] Own account linked; ordinary viewer cannot access settings or unassigned messenger.
- [ ] One read-only native/Seerr test; no accidental request, broadcast or deletion used as a test.
- [ ] If Activity is enabled: real Discord launch, mapping, OAuth, linking and permitted browse tested.
- [ ] Plugin state backed up consistently with its key; privacy/retention explained to users.

This completes the **onboarding walkthrough**, not production approval. Remaining gates are in
[ACCEPTANCE.md](ACCEPTANCE.md) and [FEATURES.md](FEATURES.md).

Portal wording can change. Official references checked 16 October 2026:
[bot installation](https://docs.discord.com/developers/quick-start/getting-started),
[Activity/Launch/OAuth setup](https://docs.discord.com/developers/activities/building-an-activity),
[directory URL mapping and development access](https://docs.discord.com/developers/activities/development-guides/local-development#url-mapping).
Use their Portal explanations. Media Hub's packaged frontend and API already run in Jellyfin;
the example apps' deployment instructions are not needed for this plugin.
