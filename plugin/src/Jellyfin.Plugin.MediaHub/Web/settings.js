(function () {
    'use strict';
    window.JellyfinMediaSettings = function (view, api, verifyAccount) {
        const section = view.querySelector('[data-settings]'), form = section.querySelector('[data-configuration]');
        const host = section.querySelector('[data-settings-fields]'), selector = section.querySelector('[data-settings-section]');
        const message = section.querySelector('[data-settings-notice]'), summary = section.querySelector('[data-setup-summary]'), changes = section.querySelector('[data-settings-changes]');
        const inputs = new Map(), definitions = [], panels = new Map();
        let saved, baseline, active = false, busy = false, checking = false, conflict = false, version = 0, edits = 0, rowNumber = 0, approvals;
        if (window.MediaHubNativeUrl) return { ready() {}, verifyAccess() {}, authorized: () => false, hide() { section.hidden = true; } };
        form.noValidate = true;
        function button(text, action) {
            const b = document.createElement('button', 'emby-button'); b.setAttribute('is', 'emby-button'); b.type = 'button'; b.className = 'raised emby-button'; b.textContent = text;
            b.addEventListener('click', () => { if (allowed()) try { action(); } catch (error) { message.textContent = error.message; } }); return b;
        }
        function allowed() { if (!active) return false; try { verifyAccount(); return true; } catch { return false; } }
        function option(input, value, label) { const o = document.createElement('option'); o.value = String(value); o.textContent = label; input.append(o); }
        function selected(input, value, label) {
            input.replaceChildren(); option(input, '', 'Choose after loading saved service options');
            if (value !== '' && value !== null && value !== undefined && value !== 0) option(input, value, label || 'Saved selection · load options to verify');
            input.value = String(value || '');
        }
        function group(id, title, description) {
            const panel = document.createElement('div'); panel.className = 'verticalSection'; panel.dataset.settingsPanel = id;
            const h = document.createElement('h3'); h.className = 'sectionTitle'; h.textContent = title;
            const p = document.createElement('p'); p.className = 'fieldDescription'; p.textContent = description; panel.append(h, p); host.append(panel); panels.set(id, panel); return panel;
        }
        function field(panel, name, label, type, help, options, min, max) {
            const select = type === 'select' || type === 'choice';
            const kind = select ? 'emby-select' : 'emby-input';
            const container = document.createElement('div'); container.className = select ? 'selectContainer' : 'inputContainer';
            // Jellyfin's v0 createdCallback needs the field's parent for its native label.
            // Serialize attributes with the DOM, then parse native markup inside that parent.
            const attributes = document.createElement(select ? 'select' : 'input'); attributes.setAttribute('label', label);
            attributes.name = name; attributes.id = 'hubSetting-' + name;
            container.insertAdjacentHTML('beforeend', attributes.outerHTML.replace(/^<([a-z]+)/, '<$1 is="' + kind + '"'));
            const input = container.querySelector(select ? 'select' : 'input'); input.setAttribute('aria-describedby', input.id + '-help');
            if (!select) { input.type = type; input.maxLength = type === 'password' ? 4096 : 2048; if (type === 'password') input.autocomplete = 'new-password'; }
            if (type === 'number') { input.min = min; input.max = max; input.step = '1'; input.required = true; }
            if (select) for (const [value, text] of options || [['', 'Load saved service options first']]) option(input, value, text);
            const explanation = document.createElement('p'); explanation.id = input.id + '-help'; explanation.className = 'fieldDescription'; explanation.textContent = help; explanation.hidden = true;
            const helpButton = button('Help', () => { explanation.hidden = !explanation.hidden; helpButton.setAttribute('aria-expanded', String(!explanation.hidden)); });
            helpButton.className = 'emby-button button-link'; helpButton.setAttribute('aria-label', 'Help for ' + label); helpButton.setAttribute('aria-controls', explanation.id); helpButton.setAttribute('aria-expanded', 'false');
            container.append(helpButton, explanation); panel.append(container);
            inputs.set(name, input); definitions.push({ name, label, panel: panel.dataset.settingsPanel, type }); return input;
        }
        function secret(panel, name, label, help) {
            const input = field(panel, name, label, 'password', help);
            const action = field(panel, name + 'Action', 'Saved ' + label.toLowerCase() + ' handling', 'select', 'Keep leaves the saved secret unchanged when the password field is empty. Remove explicitly deletes only this saved credential on Save; dependent features must be disabled or disconnected too. No account, request history or media is deleted.', [['keep', 'Keep saved secret · or enter a replacement above'], ['remove', 'Remove saved secret when settings are saved']]);
            action.addEventListener('change', () => { if (action.value === 'remove') input.value = ''; changed(); }); return input;
        }
        const onOff = [['false', 'Disabled'], ['true', 'Enabled']];
        const notifications = group('notifications', 'Signed notification wake-ups', 'Optional acceleration for a producer that supports Media Hub’s HMAC protocol. Regular verified polling remains enabled. No hook payload authorizes a request, watchlist mutation, playback or deletion.');
        secret(notifications, 'notificationHookKey', 'Notification signing secret', 'Use an independently generated 32–256 character high-entropy secret. Empty keeps it; explicit Remove disables hooks. Send HTTPS POST to /MediaHub/Notifications/Hook under your Jellyfin base with X-MediaHub-Timestamp (Unix seconds), X-MediaHub-Nonce (32 hex characters) and X-MediaHub-Signature (hex HMAC-SHA256 over UTF-8 “MediaHub.NotificationHook.v1” + newline + timestamp + newline + nonce + newline, followed by exact body bytes). Never put this secret in a URL. Seerr/Arr integrations without this signing protocol must use polling, not an unsigned workaround or a new sidecar.');
        const general = group('general', 'Jellyfin and Seerr', 'Use existing server addresses. Seerr remains responsible for accounts, request permissions, quotas and movie/series routing.');
        field(general, 'publicJellyfinUrl', 'Public Jellyfin address', 'url', 'Your existing HTTPS base, including any /jellyfin prefix, for example https://media.example/jellyfin. Do not append /web, a query, token or login page. Media Hub uses Jellyfin’s listener; no new port or network changes.');
        field(general, 'seerrUrl', 'Seerr server address', 'url', 'Base address reachable from the Jellyfin process, without /api/v1. Import each Jellyfin user into Seerr with its supported account workflow; matching names are not identity proof.');
        secret(general, 'seerrKey', 'Seerr API key', 'Seerr → Settings → General → API key. Empty keeps the saved key. The key is encrypted and never returned. Requests still run with the viewer’s verified Seerr account, not administrator permissions.');
        const discord = group('discord', 'Discord bot', 'One plugin instance per bot token. Changing saved connection settings reconnects the bot, not Jellyfin.');
        field(discord, 'discordEnabled', 'Bot connection', 'select', 'Enable only after saving the bot token and an explicit server allowlist. Slash commands are registered on connection; there is no registration script or incoming Interactions Endpoint.', onOff);
        field(discord, 'allowedServers', 'Allowed Discord server IDs', 'text', 'Discord → User Settings → Advanced → Developer Mode, then right-click each server → Copy Server ID. Separate IDs with commas. Blank is not allow-all. Application, role and user IDs are different.');
        secret(discord, 'discordToken', 'Discord bot token', 'Developer Portal → application → Bot → Token. This is not the Application ID or OAuth client secret. Empty keeps the saved token. Never post it in chat, URLs or support screenshots.');
        const botHelp = document.createElement('details'), botTitle = document.createElement('summary'), botText = document.createElement('p'); botTitle.textContent = 'Bot installation and permissions';
        botText.textContent = 'Developer Portal → Installation → Guild Install, scopes bot and applications.commands. Allow View Channels, Send Messages, Embed Links and Read Message History; Attach Files/Send Messages in Threads only when needed. Do not grant Administrator, Manage Server/Roles/Channels/Webhooks, mass mentions, or voice permissions. Presence intent stays off. Human testers need Use Application Commands.'; botHelp.append(botTitle, botText); discord.append(botHelp);
        const activity = group('activity', 'Account linking and Discord Activity', 'OAuth linking and optional Discord Activity use the same Discord application as the bot. Activity does not receive Jellyfin credentials or act as a native player.');
        field(activity, 'applicationId', 'Discord application ID', 'text', 'Developer Portal → General Information → Application ID (also OAuth2 Client ID). Configure it together with its OAuth client secret. This is not a Discord server ID.');
        secret(activity, 'clientSecret', 'Discord OAuth client secret', 'Developer Portal → OAuth2 → Client Secret, from the same application as the bot. Empty keeps the saved secret. Callback GET only records proof; account linking still needs explicit confirmation in your own signed-in Jellyfin browser.');
        field(activity, 'oauthPkce', 'OAuth proof protection (PKCE)', 'select', 'Keep enabled when supported. Do not disable state, cookies or identity checks to work around a proxy or account mismatch.', [['true', 'Use PKCE · recommended'], ['false', 'Provider without PKCE support']]);
        field(activity, 'activityEnabled', 'Discord Activity', 'select', 'Optional web interface inside Discord. Leave disabled until the HTTPS mapping and application setup below are ready.', onOff);
        const activityHelp = document.createElement('details'), activityTitle = document.createElement('summary'), activityText = document.createElement('p'), callback = document.createElement('p'), mapping = document.createElement('p');
        activityTitle.textContent = 'Activity and account-link connection details';
        activityText.textContent = 'Developer Portal → OAuth2 → Redirects: add the exact callback below and the SDK placeholder https://127.0.0.1 (not a new loopback service). Activities → enable supported platforms → URL Mappings: prefix /, target below without https://. Map the directory, not /App or /web. Launch through Discord’s App Launcher/managed Launch entry point; Media Hub does not register /activity. Human testers need Use Activities and allowed-server membership. Owner/team testing does not publish the Activity. Do not disable authentication, TLS or frame checks.';
        callback.dataset.settingsCallback = ''; mapping.dataset.settingsMapping = ''; activityHelp.append(activityTitle, activityText, callback, mapping); activity.append(activityHelp);
        for (const [id, service, title] of [['music', 'lidarr', 'Music requests · Lidarr'], ['books', 'readarr', 'Book requests · Readarr']]) {
            const panel = group(id, title, 'Save the address and key with requests disabled first. Load options reads only that saved service; it does not create folders, change profiles or request media.');
            field(panel, service + 'Url', service === 'lidarr' ? 'Lidarr server address' : 'Readarr server address', 'url', 'Existing service base address reachable from Jellyfin, without /api/v1. Leave unconfigured if unused. No folders or other applications are installed.');
            secret(panel, service + 'Key', service === 'lidarr' ? 'Lidarr API key' : 'Readarr API key', 'Service Settings → General → API key. Empty keeps the saved key. Save this connection before loading its folders and profiles.');
            panel.append(button('Load saved service folders and profiles', () => void loadOptions(service)));
            field(panel, service + 'Root', 'Request destination folder', 'choice', 'Choose an existing accessible root folder returned by this service. This never creates or deletes a folder.');
            field(panel, service + 'Quality', 'Quality profile', 'choice', 'Choose an existing profile by name. The saved profile is checked again before a request is sent.');
            field(panel, service + 'Metadata', 'Metadata profile', 'choice', 'Choose an existing metadata profile by name. No service defaults are changed.');
            field(panel, service + 'Requests', 'Request access', 'select', 'Requests require the viewer’s current Seerr permission too. Only a selected album or book edition is added; parent-wide searches and exclusion removal are not allowed.', [['false', 'Browse only'], ['true', service === 'lidarr' ? 'Allow selected album requests' : 'Allow selected book-edition requests']]);
            field(panel, service + 'Limit', 'Daily requests per user', 'number', 'Rolling 24-hour limit. Uncertain sends count toward this limit and are never automatically retried.', null, 1, 100);
            panel.append(button('Disconnect ' + (service === 'lidarr' ? 'Lidarr' : 'Readarr') + ' on next Save', () => {
                set(service + 'Url', ''); set(service + 'Key', ''); set(service + 'KeyAction', 'remove'); set(service + 'Requests', false);
                for (const property of ['Root', 'Quality', 'Metadata']) selected(inputs.get(service + property), ''); changed();
                message.textContent = 'Connection removal is staged only. Save to apply it. No service data, requests, watchlists or media will be deleted.';
            }));
        }
        const messaging = group('messaging', 'Messaging access and retention', 'Messaging operators can read the bot’s shared private inbox. Jellyfin administration or Discord moderation alone never grants this access.');
        field(messaging, 'operators', 'Messaging operator Discord user IDs', 'text', 'Trusted people’s user IDs, separated by commas, not roles or server IDs. Each operator still needs a verified Jellyfin link and current destination permissions. Start empty unless this feature is intentionally used.');
        field(messaging, 'seerrOperators', 'Additional messaging operators', 'select', 'Only deliberately enabled and live-verified Seerr administrators may supplement the explicit operator list. This does not grant Jellyfin users a new administrator role.', [['false', 'Explicit operator list only'], ['true', 'Also allow verified Seerr administrators']]);
        field(messaging, 'messageContent', 'Server-message content', 'select', 'Full content requires Message Content Intent in Developer Portal → Bot → Privileged Gateway Intents and here. Neither privileged intent is needed for slash commands or Activity sign-in.', [['false', 'Discord default · mentions and allowed events'], ['true', 'Retain permitted ordinary message content']]);
        field(messaging, 'membersIntent', 'All-member announcement support', 'select', 'Requires Server Members Intent in Developer Portal too. Enabling it does not send messages: announcements still need an immutable recipient preview and explicit confirmation.', onOff);
        field(messaging, 'inboxChannels', 'Retained server-channel IDs', 'text', 'Comma-separated channel IDs, copied with Developer Mode. Channel/member permissions are rechecked. The inbox retains eligible incoming events, not a complete history.');
        field(messaging, 'retention', 'Inbox retention (days)', 'number', 'Automatic retention applies only to Media Hub’s own inbox records/files, not Discord history or media.', null, 1, 365);
        field(messaging, 'maxRecipients', 'Announcement recipient limit', 'number', 'Maximum recipients in a confirmed announcement. Start small. Uncertain sends are not replayed automatically.', null, 1, 1000);
        const removal = group('removal', 'Media removal and consent', 'Only original requesters can propose removal. Current recent viewers must unanimously agree or age out, followed by a full delay. Administrators cannot bypass ownership.');
        field(removal, 'automaticDeletion', 'Approved media removal', 'select', 'Keep disabled during setup. Enabled removal uses verified Radarr/Sonarr file APIs for due approved proposals only; no direct filesystem, root-folder, request-history or broad record deletion.', [['false', 'Do not automatically remove media'], ['true', 'Remove due, fully verified proposals']]);
        field(removal, 'recentDays', 'Recent-viewer consent window (days)', 'number', 'Default 14 days. New activity invalidates old consent. Incomplete or unverifiable activity scans block removal.', null, 1, 3650);
        field(removal, 'deletionDays', 'Minimum removal delay (days)', 'number', 'Default 2 full days after eligibility. Personal increases extend pending proposals; no saved setting silently shortens an existing deadline.', null, 1, 3650);
        const destinations = group('destinations', 'Approved Radarr and Sonarr servers', 'Discovery reads only saved Seerr configuration. Verify each exact address independently before adding it. New addresses are never approved or contacted automatically.');
        destinations.append(button('Find servers configured in saved Seerr', () => void discover()));
        const candidates = document.createElement('div'); candidates.dataset.settingsCandidates = ''; destinations.append(candidates);
        approvals = document.createElement('div'); approvals.dataset.settingsApprovals = ''; destinations.append(approvals);
        destinations.append(button('Add an approved server manually', () => addApproval({ Service: '', Instance: 0, Url: '' })));
        function addApproval(value) {
            if (approvals.children.length >= 100) throw new Error('At most 100 approved servers are allowed.');
            if (value.Service && [...approvals.children].some(row => row.querySelector('select').value === value.Service && Number(row.querySelector('input[type="number"]').value) === value.Instance)) throw new Error('This server instance already has an approval. Remove its old approval explicitly before changing its address.');
            const row = document.createElement('div'); row.className = 'verticalSection'; row.dataset.approvalRow = '';
            const id = ++rowNumber;
            function editor(type, label, value) {
                const kind = type === 'select' ? 'emby-select' : 'emby-input', c = document.createElement('div'); c.className = type === 'select' ? 'selectContainer' : 'inputContainer';
                const attributes = document.createElement(type === 'select' ? 'select' : 'input'); attributes.setAttribute('label', label); attributes.id = 'hubApproval-' + id + '-' + type;
                c.insertAdjacentHTML('beforeend', attributes.outerHTML.replace(/^<([a-z]+)/, '<$1 is="' + kind + '"')); const input = c.querySelector(type === 'select' ? 'select' : 'input'); input.required = true;
                if (type === 'select') for (const [v, text] of [['', 'Choose a service'], ['radarr', 'Radarr · movies'], ['sonarr', 'Sonarr · series']]) option(input, v, text);
                else { input.type = type; if (type === 'number') { input.min = '0'; input.max = '2147483647'; input.step = '1'; } else input.maxLength = 2048; }
                input.value = value; row.append(c); return input;
            }
            editor('select', 'Media service', value.Service); editor('number', 'Seerr server instance ID', value.Instance); editor('url', 'Independently approved base address', value.Url);
            row.append(button('Remove this approval', () => { row.remove(); changed(); })); approvals.append(row); changed();
        }
        const get = name => inputs.get(name).value, set = (name, value) => { inputs.get(name).value = String(value ?? ''); };
        const flag = name => get(name) === 'true';
        const ids = name => get(name).split(',').map(s => s.trim()).filter(Boolean);
        const secretValue = name => get(name + 'Action') === 'remove' ? '' : get(name) || null;
        function destinationsValue() { return [...approvals.children].map(row => ({ Service: row.querySelector('select').value, Instance: Number(row.querySelector('input[type="number"]').value), Url: row.querySelector('input[type="url"]').value.trim() })); }
        function payload() {
            const service = name => ({ Url: get(name + 'Url').trim(), Key: secretValue(name + 'Key'), Root: get(name + 'Root'), Quality: Number(get(name + 'Quality')), Metadata: Number(get(name + 'Metadata')), Requests: flag(name + 'Requests'), DailyLimit: Number(get(name + 'Limit')) });
            return { Revision: saved.Revision, DiscordEnabled: flag('discordEnabled'), PublicJellyfinUrl: get('publicJellyfinUrl').trim(), AllowedGuilds: ids('allowedServers'), SeerrUrl: get('seerrUrl').trim(), DiscordToken: secretValue('discordToken'), SeerrKey: secretValue('seerrKey'),
                RecentActivityDays: Number(get('recentDays')), DeletionDelayDays: Number(get('deletionDays')), AutomaticDeletion: flag('automaticDeletion'), Features: {
                    Lidarr: service('lidarr'), Readarr: service('readarr'), Operators: ids('operators'), SeerrOperators: flag('seerrOperators'), MessageContent: flag('messageContent'), MembersIntent: flag('membersIntent'), InboxChannels: ids('inboxChannels'), MessageRetentionDays: Number(get('retention')), MaxRecipients: Number(get('maxRecipients')), DiscordApplicationId: get('applicationId').trim(), DiscordClientSecret: secretValue('clientSecret'), ActivityEnabled: flag('activityEnabled'), OAuthPkce: flag('oauthPkce'), ArrDestinations: destinationsValue(), NotificationHookKey: secretValue('notificationHookKey') } };
        }
        function snapshot() { return new Map(definitions.map(d => [d.name, get(d.name)]).concat([['destinations', JSON.stringify(destinationsValue())]])); }
        function changed() {
            edits++; if (!saved) return;
            changes.replaceChildren(); const now = snapshot();
            for (const d of definitions.concat([{ name: 'destinations', label: 'Approved media servers' }])) if (now.get(d.name) !== baseline?.get(d.name)) { const li = document.createElement('li'); li.className = 'listItem'; li.textContent = d.label; changes.append(li); }
            controls();
        }
        function controls() {
            form.setAttribute('aria-busy', String(busy));
            form.querySelectorAll('input,select,button').forEach(input => { input.disabled = busy; }); selector.disabled = busy;
            for (const d of definitions) if (d.type === 'password') inputs.get(d.name).disabled = busy || get(d.name + 'Action') === 'remove';
            if (saved?.DiscordConfigurationOwnedByAdapter) for (const key of ['discord', 'activity', 'messaging']) panels.get(key).querySelectorAll('input,select,button').forEach(input => { input.disabled = true; });
            form.querySelector('[type="submit"]').disabled = busy || conflict || !saved || saved.Revision > 0 && !saved.ConfigurationReviewRequired && changes.childElementCount === 0;
        }
        function draw() { for (const [key, panel] of panels) panel.hidden = key !== selector.value; }
        function ready(value) {
            if (window.MediaHubNativeUrl || !Number.isInteger(value.Revision) || value.Revision < 0) return;
            saved = value; active = true; section.hidden = false; busy = false; conflict = false;
            for (const d of definitions) if (d.type === 'password') { set(d.name, ''); set(d.name + 'Action', 'keep'); }
            const f = value.Features || {};
            for (const [name, entry] of Object.entries({ publicJellyfinUrl: value.PublicJellyfinUrl, seerrUrl: value.SeerrUrl, allowedServers: (value.AllowedGuilds || []).join(', '), discordEnabled: Boolean(value.DiscordEnabled), automaticDeletion: Boolean(value.AutomaticDeletion), recentDays: value.RecentActivityDays ?? 14, deletionDays: value.DeletionDelayDays ?? 2,
                operators: (f.Operators || []).join(', '), inboxChannels: (f.InboxChannels || []).join(', '), seerrOperators: Boolean(f.SeerrOperators), messageContent: Boolean(f.MessageContent), membersIntent: Boolean(f.MembersIntent), retention: f.MessageRetentionDays ?? 7, maxRecipients: f.MaxRecipients ?? 100, applicationId: f.DiscordApplicationId || '', activityEnabled: Boolean(f.ActivityEnabled), oauthPkce: f.OAuthPkce !== false })) set(name, entry);
            for (const name of ['lidarr', 'readarr']) {
                const s = f[name === 'lidarr' ? 'Lidarr' : 'Readarr'] || {}; set(name + 'Url', s.Url || ''); set(name + 'Requests', Boolean(s.Requests)); set(name + 'Limit', s.DailyLimit ?? 10);
                for (const property of ['Root', 'Quality', 'Metadata']) selected(inputs.get(name + property), s[property] || '', property === 'Root' ? s.Root : 'Saved profile #' + s[property]);
            }
            approvals.replaceChildren(); candidates.replaceChildren(); for (const row of f.ArrDestinations || []) addApproval(row);
            baseline = snapshot(); changes.replaceChildren(); message.textContent = value.ConfigurationReviewRequired ? 'Older unsigned settings require explicit review and Save. Integrations remain blocked until reviewed.' : 'Saved configuration loaded. Connection tests are separate from saving.';
            summary.textContent = value.Revision === 0 ? 'No settings saved yet. Choose the connections you want; optional features stay disabled until you explicitly save them.' : 'Saved revision ' + value.Revision + ' · Discord bot ' + (value.DiscordEnabled ? 'enabled' : 'disabled') + ' · Activity ' + (f.ActivityEnabled ? 'enabled' : 'disabled') + ' · automatic media removal ' + (value.AutomaticDeletion ? 'enabled' : 'disabled') + '. This is configuration status, not a live connection test.';
            section.querySelector('[data-discord-ownership]')?.remove();
            if (value.DiscordConfigurationOwnedByAdapter) {
                summary.textContent = 'Core revision ' + value.Revision + '. Discord fields here are retained legacy values; manage active Discord configuration in the separate plugin.';
                const link = document.createElement('a'); link.dataset.discordOwnership = ''; link.className = 'button-link'; link.textContent = 'Configure the separate Discord Integration';
                link.href = '#/configurationpage?name=mediahub-discord'; summary.after(link);
            }
            callback.textContent = value.PublicJellyfinUrl ? 'OAuth callback: ' + value.PublicJellyfinUrl.replace(/\/$/, '') + '/MediaHub/Link/OAuth/Callback' : 'Save the public Jellyfin address to display its callback.';
            mapping.textContent = value.PublicJellyfinUrl ? 'Activity mapping target: ' + value.PublicJellyfinUrl.replace(/^https:\/\//, '').replace(/\/$/, '') + '/MediaHub/Activity/' : '';
            draw(); controls();
        }
        async function operation(action) {
            if (!allowed() || busy) return; busy = true; const run = version, draft = edits; controls();
            try { await action(() => active && run === version && draft === edits); }
            catch (error) { if (run === version) { if ([401, 403].includes(error.status)) hide(); else message.textContent = error.message; } }
            finally { if (run === version) { busy = false; controls(); } }
        }
        async function loadOptions(name) {
            return operation(async current => {
                const s = saved.Features?.[name === 'lidarr' ? 'Lidarr' : 'Readarr'];
                if (!s?.Url || !s.HasKey || get(name + 'Url') !== s.Url || get(name + 'Key') || get(name + 'KeyAction') !== 'keep') throw new Error('Save this service address and API key with requests disabled before loading its options.');
                const result = await api('Configuration/Options/' + name + '?revision=' + saved.Revision);
                if (!current() || !allowed()) return;
                for (const [property, rows] of [['Root', result.Roots], ['Quality', result.QualityProfiles], ['Metadata', result.MetadataProfiles]]) {
                    const input = inputs.get(name + property), before = input.value; input.replaceChildren(); option(input, '', 'Choose an existing ' + (property === 'Root' ? 'folder' : 'profile'));
                    for (const choice of rows) option(input, choice.Value, choice.Label);
                    if (before && ![...input.options].some(o => o.value === before)) option(input, before, 'Saved choice is unavailable · select another');
                    input.value = before;
                }
                message.textContent = 'Saved service options loaded. No selection was changed and nothing was saved.';
            });
        }
        async function verifyAccess() {
            if (!active || busy || checking || !allowed()) return;
            checking = true; const run = version, revision = saved.Revision;
            try {
                const current = await api('Settings');
                if (active && run === version && allowed() && saved.Revision === revision && current.Revision !== revision) {
                    conflict = true; message.textContent = 'Server settings changed elsewhere. Your drafts were kept. Explicitly reload and review before saving.'; controls();
                }
            } catch { if (run === version) hide(); }
            finally { if (run === version) checking = false; }
        }
        async function discover() {
            return operation(async current => {
                if (!saved.SeerrUrl || !saved.HasSeerrKey || get('seerrUrl') !== saved.SeerrUrl || get('seerrKey') || get('seerrKeyAction') !== 'keep') throw new Error('Save the Seerr address and API key before finding its configured servers. Unsaved credentials are never used for discovery.');
                const rows = await api('Configuration/Destinations?revision=' + saved.Revision); if (!current() || !allowed()) return; candidates.replaceChildren();
                for (const row of rows) {
                    const box = document.createElement('div'); box.className = 'verticalSection'; const p = document.createElement('p'); p.textContent = row.Service + ' · Seerr instance ' + row.Instance + ' · ' + row.Url;
                    box.append(p, button('Add this independently verified address to the approval list', () => { try { addApproval(row); } catch (e) { message.textContent = e.message; } })); candidates.append(box);
                }
                message.textContent = rows.length ? 'Found in Seerr only. Verify addresses independently, add only those you approve, then Save server settings. No Arr server was contacted.' : 'No Radarr or Sonarr servers are configured in saved Seerr.';
            });
        }
        function hide() {
            version++; active = false; busy = false; checking = false; conflict = false; saved = baseline = undefined; section.hidden = true;
            form.reset(); for (const input of inputs.values()) if (input.type === 'password') input.value = '';
            for (const name of ['lidarr', 'readarr']) for (const property of ['Root', 'Quality', 'Metadata']) selected(inputs.get(name + property), '');
            approvals.replaceChildren(); candidates.replaceChildren(); changes.replaceChildren(); message.textContent = summary.textContent = callback.textContent = mapping.textContent = '';
            for (const p of host.querySelectorAll('.fieldDescription[id]')) p.hidden = true;
            for (const b of host.querySelectorAll('[aria-expanded]')) b.setAttribute('aria-expanded', 'false');
            view.dispatchEvent(new window.Event('mediahubsettingshidden'));
        }
        selector.addEventListener('change', () => { if (allowed()) draw(); });
        form.addEventListener('input', changed); form.addEventListener('change', changed);
        form.addEventListener('submit', event => {
            event.preventDefault();
            if (!allowed() || busy || conflict) return;
            const invalid = [...form.querySelectorAll('input,select')].find(input => !input.disabled && !input.checkValidity());
            if (invalid) { selector.value = invalid.closest('[data-settings-panel]').dataset.settingsPanel; draw(); invalid.reportValidity(); return; }
            const removed = definitions.filter(d => d.type === 'password' && get(d.name + 'Action') === 'remove').map(d => d.label);
            if (removed.length && !window.confirm('Remove these saved credentials when saving all settings: ' + removed.join(', ') + '? No accounts, media or request history will be deleted.')) return;
            void operation(async current => {
                const result = await api('Settings', payload()); if (!current() || !allowed()) return; ready(result); message.textContent = 'Server settings saved together. Connection health and provider acceptance are separate checks.';
            });
        });
        section.querySelector('[data-settings-reload]').addEventListener('click', () => {
            if (changes.childElementCount && !window.confirm('Discard all unsaved server settings, including other sections?')) return;
            void operation(async current => { const result = await api('Settings'); if (current() && allowed()) ready(result); });
        });
        draw(); return { ready, hide, verifyAccess, authorized: () => active };
    };
})();
