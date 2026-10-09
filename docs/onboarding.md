# Onboarding & local admin panel

The bot now has **one configuration panel on a separate HTTP port**. It handles first-run setup, saved settings, connection tests and admin messaging/inbox. Routine settings no longer need `.env`. User registration still happens through Discord `/link` or Activity Quick Connect—not through a browser password form.

## Start with a preset

### Local development (Python 3.14)

```sh
python -m pip install -r requirements.txt
python bot.py
```

No `.env` is required. Startup prints a URL such as `http://127.0.0.1:43127` and a **one-time access code**. Open that URL on the same machine and enter the code. The bot waits for valid setup before connecting to Discord. The panel remains available after the bot starts.

Optional bootstrap options are in [`.env.example`](../.env.example): `DATA_DIR`, `PANEL_ENABLED`, `PANEL_HOST`, `PANEL_PORT`. Port `0` selects a random available port. A fixed port can be useful behind an SSH forward. Windows can use `py` instead of `python`.

### Docker: slash-only

```sh
docker compose -f templates/compose.slash.yml up -d --build
docker compose -f templates/compose.slash.yml port media 8787
docker compose -f templates/compose.slash.yml logs media
```

Open the **host port** reported by `port`, using `http://127.0.0.1:PORT`; the code is in the logs. The container listens on `8787`, published to a random host-loopback port. It uses persistent `/data` owned by UID `10001`. No Activity backend port is published by this template.

### Docker: Activity / webhooks

```sh
docker compose -f templates/compose.activity.yml up -d --build
docker compose -f templates/compose.activity.yml port media 8787
docker compose -f templates/compose.activity.yml logs media
```

This additionally publishes the **separate bot backend** at host `127.0.0.1:8080`. Choose the Activity, webhooks, or combined preset in the wizard. Tunnel/proxy **8080**, never the admin panel port. Keep only one template/instance running against the database; stop the old container before switching presets.

| Wizard preset | What it enables | What you need |
| --- | --- | --- |
| Slash only | Full private command/component hub and local panel | Discord bot + Seerr; no public hosting |
| Activity | Slash commands plus embedded dashboard | Built frontend, OAuth client secret, public HTTPS, Activity URL Mapping |
| Slash + webhooks | Slash commands plus incoming verified refresh signals | Receiver reachable from each configured service; generated webhook credential |
| Activity + webhooks | Both | Both sets of requirements above |

Docker builds the frontend automatically. Native Activity installs also run `npm ci` and `npm run build` in `activity/`. The onboarding panel itself has no Node build dependency.

## Secure access

- The one-time code grants **local operator authority**: editing configuration, adding bot-only exceptions, reading the shared inbox and sending confirmed announcements. It is separate from Seerr/Discord viewer authentication. Local sends are audited as `local-panel`, never attributed to a Discord user.
- Default binding is loopback. For Docker, `0.0.0.0` is only the **container bind**; the supplied templates publish it to **host loopback**. Do not remove that restriction accidentally.
- Startup prints the listener's bind address separately from the local browser URL. For an existing private container IP with no published panel port, forward directly to that IP over SSH; see [DNS and panel reachability checks](setup.md#docker-dns-failures-and-private-panel-access).
- For a remote host, use an SSH tunnel: `ssh -L 8787:127.0.0.1:HOST_PANEL_PORT user@host`, then open `http://127.0.0.1:8787`. The panel supports direct private-IP access when explicitly bound/published on your LAN, but plain HTTP is unencrypted—SSH forwarding is safer.
- Never expose this panel to the public Internet, configure it in Discord URL Mapping, or share its code/logs. Its browser requests require same-origin JSON, sessions use HttpOnly/SameSite=Strict cookies, public/DNS-rebinding hostnames are rejected, and pages cannot be framed.
- The code is consumed at login. Sessions expire after **8 hours** (live updates do not extend this deadline). When the last session expires, the next panel API request prints one replacement code in the service console; enter it in the login form without restarting the bot. If a still-valid cookie was lost, restart the service to obtain a new code. An authenticated session stays valid across **Save & apply** bot restarts. Service/process restarts invalidate it and print a new startup code.
- Always use the same browser URL, such as `http://127.0.0.1:8787` through your SSH tunnel; `localhost`, another IP, or another forwarded port is a different origin. Cookies are HttpOnly/SameSite=Strict and named per origin so other panels on localhost cannot overwrite them. No cookie/session credential is stored in URLs or browser local storage. A first visit without a cookie shows the sign-in form, not a false expiry warning. Actual expiry removes private content and brings back that form; wrong/used codes remain retryable. Tunnel/backend interruptions show reconnection errors, not session-expired messages, and do not automatically resend chat or announcements.

## Wizard: verified steps

### 1. Discord

Create the application and bot in [Developer Portal](https://discord.com/developers/applications). Paste its **bot token** into the panel. Verify checks the bot identity/application, lists its joined guilds, checks requested privileged-intent flags and bot-only exception IDs, and fills the Application ID automatically.

Use the returned install link or **Installation → Guild Install** with scopes `bot`, `applications.commands`. Grant **View Channels**, **Send Messages**, **Embed Links**, **Read Message History**, and **Attach Files** for chat uploads; optionally **Send Messages in Threads**. No Administrator/Manage Roles/Manage Messages permission is needed. Invite it to every selected server, then verify again. Paste allowed guild IDs separated by commas; empty allows all guilds/DMs.

In Discord **User Settings → Advanced**, enable Developer Mode to copy server/channel/user IDs. In **Bot → Privileged Gateway Intents**:

- **Server Members** only for all-member broadcast DMs; enable the matching **All-member DMs** switch in the panel.
- **Message Content** only for full non-mention reply/selected-channel inbox content; enable **Full inbox content** in the panel. Ordinary DMs/direct mentions do not need it.
- **Presence** stays off. Larger/verified apps may need Discord approval for privileged intents.

The first token verification can succeed with no joined servers, so you can obtain the invite link. Set a guild allowlist only after inviting; verification rejects selected guilds the bot has not joined. Test actual channel permissions later with a single-recipient send—application flags alone cannot prove effective channel overrides.

### 2. Seerr, admins and movie/TV services

Enter Seerr's reachable base URL and administrator API key. Verify checks administrator API access, reads **all Radarr/Sonarr instances**, including default/nondefault/4K servers, and tests each instance's status and diskspace API. The URL, SSL, port, base path and key come from Seerr; imported keys are **not copied into the browser or settings file**. The bot re-discovers these settings before every media refresh; failed discovery blocks operations instead of using old connections.

Configure Radarr/Sonarr **in Seerr**, not the bot. Seerr still routes movie/TV requests and file deletion. Its addresses must work from the bot's container/machine; `localhost` inside a container points at that container. Do not copy an internal service URL into a public Activity mapping.

Admin access follows **verified linked Seerr owners/Administrator users**. The wizard lists the current Seerr admins. Each must complete `/link` (or Activity Quick Connect) to prove which Discord account they own; the bot never trusts a display-name match or automatically adopts an unverified `discordIds` entry. Admin actions recheck the authenticated account's live permissions/link. Seerr outages do not grant cached admin authority.

To give someone **bot-only admin access**, enter their copied Discord ID under **Bot-only admin IDs** and verify Discord again. These exceptions bypass the Seerr admin-role requirement, **not identity verification**: they must complete `/link` and retain a valid Jellyfin user token. Removing an exception takes effect after Save & apply. The wizard does **not** promote users in Seerr; manage Seerr roles in Seerr's Users page. Both types of bot admin have global bot messaging/inbox access, not just their home guild. The console-code-authenticated local panel is a separate operator trust path.

Enable Quick Connect in Jellyfin and configure Seerr's Jellyfin server. `/link` verifies that flow after startup; the API-key test does not prove a user's Quick Connect approval. The bot discovers the internal address, server ID, and Browser destination from Seerr's Jellyfin settings. Set Seerr's external Jellyfin hostname to a URL users can reach; otherwise Browser falls back to Seerr's internal address. Optional extra link labels/URLs can be configured here; `{}` uses the automatic Browser link only. These are browser/network destinations, not remote playback devices; omit `/web`.

Jellyfin is the identity source of truth. The approved user token is encrypted in SQLite; its encryption key is `jellyfin.key` beside the database. Protect and back up both. Before any private server details, media images or links are shown, `/Users/Me` must confirm the same enabled Jellyfin user. Token rejection requires a new `/link`; connectivity failures block disclosure but preserve credentials. Existing pre-token links need one re-verification. Seerr permissions/quotas are still checked independently for media actions.

### 3–4. Lidarr / Readarr (optional)

These remain **direct** connections. Enter the URL/key pair or leave both blank. Verify tests their API and loads existing root folders, quality profiles and metadata profiles into dropdowns. Select values and verify again. Unset profiles allow browsing but block additions. Readarr requires compatible v1 APIs and working metadata providers.

There is no automatic approval queue for albums/books; linked users need Seerr REQUEST permission and are subject to the bot's separate rolling daily limit. Use **Advanced** for that limit. No setup test adds media or deletes files.

### 5. Hosting (optional)

**Slash-only:** leave Activity off and the webhook credential empty. Media polling still runs; nothing public is required.

**Activity:** enable it, supply the OAuth client secret for the same application, and build the frontend. Publish the **bot backend**, not the local panel, through HTTPS. In Developer Portal:

1. **Activities → Settings:** enable Activities.
2. **Activities → URL Mappings:** prefix `/`, target your HTTPS hostname (no scheme in a hostname-only field).
3. **OAuth2 → Redirects:** `https://127.0.0.1` as the SDK placeholder.
4. Allow human launchers **Use Activities / Use Embedded Activities** and **Use Application Commands** in the channel.
5. Initially test as the application owner/team; broader distribution depends on Discord's Activity release settings.

Enter the backend HTTPS URL under **Public backend URL**, then Save & apply. **Test public backend** checks that the hostname serves this application's configuration. Finally launch `/activity` in Discord and complete OAuth; Refresh status shows **Discord Activity authentication verified** only when the backend has a current authenticated SDK session. An HTTP reachability check alone cannot prove Discord URL Mapping/OAuth works. No Interactions Endpoint URL is needed for this gateway-based bot.

**Webhooks:** generate a credential, Save & apply, and use **Show webhook credential** when you need to copy it. Public verification sends that shared credential only to the HTTPS hostname you configured, with redirects disabled; enter only a receiver you control. It requires an echoed random test marker from this backend. Local/private receivers may use service Test buttons without a public HTTPS URL.

Configure each service's native webhook settings:

- Radarr/Sonarr/Lidarr/Readarr: **Settings → Connect → Webhook**, URL `BASE/webhooks/radarr` (or matching source), POST, Basic username `bot`, password the credential. Enable relevant import/upgrade/deletion events and **Test**.
- Seerr: **Settings → Notifications → Webhook**, URL `BASE/webhooks/seerr`, Authorization `Bearer CREDENTIAL`, payload `{"notification_type":"{{notification_type}}"}`.

Use **Test** in every service and then Refresh status in the panel: it shows the last authenticated receiver Test timestamp per source. Receiver reachability does not prove the service is configured correctly, and a timestamp is not independent proof of a sender's identity. Real event payloads only trigger a verified API refresh. The wizard does not overwrite service notification settings.

The Activity and webhook receiver currently share one **Public backend URL** for the wizard's HTTPS checks; distinct service receiver URLs can still be configured manually. For tunnels on your host, e.g. `cloudflared tunnel --url http://127.0.0.1:8080`, use the generated backend hostname. Quick-tunnel hostnames change on restart; use a stable tunnel for ongoing deployment.

## Save & apply / administration

**Save & apply** requires current Discord and Seerr tests, plus every enabled direct Lidarr/Readarr connection. Checks expire after **10 minutes** or relevant input changes. Settings are validated and saved atomically; the Discord bot is gracefully restarted, while the local panel/session stays running. In-flight unconfirmed remote writes are never automatically replayed; pending jobs persist. If bot login/backend startup fails, the panel remains available with an error so you can correct it.

The **Messages** tab is a themed Discord-style messenger. Open a DM or categorized server channel and reply inline as the bot; normal chat targets one destination. Choose **Announcement** for a previewed/confirmed send, using **Choose destinations** to select servers, channels or a DM. **# announcements** displays history and delivery states as its own feed. Images/files/embeds render through authenticated panel routes, with Markdown and resolved tags. **Enter** sends normal chat; **Shift+Enter** inserts a line break. Live updates use session-authenticated SSE on the same private listener and preserve your draft. Leaving Messages disconnects the subscription. Local sends are audited as `local-panel`; ordinary Discord/Activity admin sends remain attributed to their verified Discord IDs. Root-panel access is not a user-registration identity.

Test a DM reply and a small image/file against your Discord deployment, then confirm one announcement to an expected channel. Check edits/deletes and the live delivery badges. Enable Full inbox content/Discord Message Content Intent for ordinary server chat, and Attach Files permission on target channels. The panel displays retained messages, not a complete historical Discord backfill. Upload limits are 4 files, 8 MiB each, 16 MiB total, with 64 MiB total retained file storage. Unsaved uploads expire after 15 minutes; sent files share inbox retention. The SQLite backup now contains file bytes, so keep it private. Details: [admin messaging](../README.md#admin-messages--inbox).

In Discord run `/link`, `/status`, `/dashboard`, `/requests`, `/library`, `/storage`, `/notifications`, `/watchlist`. Admin tools remain available through `/inbox`, `/servers`, `/announce`, `/deliveries`; see the [slash-command parity table](../README.md#slash-only-toggle). Test one expected recipient before broadcasts, and disposable media before scheduled deletion. Notifications stay opt-in; inbox retention and limits remain configurable in Advanced.

## Saved settings, overrides and migration

- Settings live in **`DATA_DIR/settings.json`**. It contains credentials in plaintext, like `.env`; Unix writes use mode `0600`. Protect the data volume/backups and host ACLs (especially Windows). It is excluded from Git/Docker context. Credentials are never returned to ordinary Activity clients; setup forms do not read stored secrets back. The webhook credential is revealed only on an explicit authenticated local action.
- Precedence: **environment overrides saved settings, which override built-in defaults**. Existing environment-based deployments still work. The panel marks overridden fields read-only; remove an override from Compose/`.env` and restart to manage it in the panel. Saved configuration never rewrites your environment.
- [`.env.example`](../.env.example) contains only panel/data bootstrap options. [`.env.advanced.example`](../.env.advanced.example) retains per-option documentation, units, defaults and dependencies and also drives panel field help. Normally leave advanced settings out of `.env`.
- Legacy direct Radarr/Sonarr variables are ignored while `SEERR_DISCOVERY=true`. Only set `SEERR_DISCOVERY=false` deliberately if you need legacy direct integration configuration. `SEERR_ADMINS=false` disables role inference; explicit bot-only exceptions still work.
- To run without the panel after onboarding, set `PANEL_ENABLED=false`. The saved credentials/configuration still load normally. Re-enable it and restart to administer locally. A fully headless legacy install can copy `.env.advanced.example` into `.env`, fill the required credentials, and add `PANEL_ENABLED=false`.
- Existing SQLite accounts/history are not deleted or relinked. Read-only storage must still be repaired; see [storage permissions](setup.md#sqlite-storage-permissions). Back up both SQLite data/sidecars and `settings.json`. Invalid configuration files fail validation, rather than being silently replaced.

For the full Discord installation/security checklist, [docs/setup.md](setup.md) remains the detailed reference. Activities always require a reachable HTTPS backend; the native bot and local panel do not require a public IP.
