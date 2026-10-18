/* global define, ApiClient */
(function () {
    'use strict';
    const create = function (view, api, identity) {
        const form = view.querySelector('[data-discord-configuration]'), host = view.querySelector('[data-discord-fields]'), message = view.querySelector('[data-discord-notice]');
        let account, revision, version = 0, active = false, busy = false, uncertain = false, dirty = false, migration = false;
        const fields = new Map();
        const errorText = error => error?.responseJSON?.Error || error?.message || 'Discord settings verification is unavailable.';
        for (const [name, label, type] of [
            ['DiscordEnabled', 'Bot connection', 'bool'], ['AllowedGuilds', 'Allowed Discord server IDs', 'ids'], ['BotToken', 'Bot token (blank keeps saved)', 'password'], ['RemoveBotToken', 'Remove saved bot token on Save', 'bool'],
            ['ApplicationId', 'Discord application ID', 'text'], ['ClientSecret', 'OAuth client secret (blank keeps saved)', 'password'], ['RemoveClientSecret', 'Remove saved client secret on Save', 'bool'],
            ['ActivityEnabled', 'Enable Discord Activity', 'bool'], ['OAuthPkce', 'Use OAuth PKCE', 'bool'], ['MessageContent', 'Message Content intent', 'bool'], ['MembersIntent', 'Server Members intent', 'bool'],
            ['Operators', 'Messaging operator Discord user IDs', 'ids'], ['SeerrOperators', 'Also allow verified Seerr administrators as operators', 'bool'], ['InboxChannels', 'Retained channel IDs', 'ids'],
            ['MessageRetentionDays', 'Private inbox retention (days)', 'number'], ['MaxRecipients', 'Announcement recipient limit', 'number']
        ]) {
            const box = document.createElement('div'); box.className = type === 'bool' ? 'selectContainer' : 'inputContainer';
            const node = document.createElement(type === 'bool' ? 'select' : 'input'); node.name = name; node.id = 'discord-' + name; node.setAttribute('label', label);
            if (type === 'bool') { for (const [value, text] of [['false', 'Disabled'], ['true', 'Enabled']]) { const option = document.createElement('option'); option.value = value; option.textContent = text; node.append(option); } }
            else { node.type = type === 'password' ? 'password' : type === 'number' ? 'number' : 'text'; node.maxLength = type === 'password' ? 4096 : 2100; if (type === 'password') node.autocomplete = 'new-password'; }
            box.insertAdjacentHTML('beforeend', node.outerHTML.replace(/^<([a-z]+)/, '<$1 is="' + (type === 'bool' ? 'emby-select' : 'emby-input') + '"'));
            const input = box.firstElementChild; fields.set(name, { input, type }); host.append(box);
        }
        const help = document.createElement('details'); help.innerHTML = '<summary>Help and migration safety</summary>';
        const text = document.createElement('p'); text.className = 'fieldDescription'; text.textContent = 'IDs are comma-separated, never allow-all. Privileged intents also require Discord Developer Portal approval. Activity uses the same application as the bot and never receives native credentials. Keep PKCE on. The first explicit Save copies reviewed legacy settings into this plugin’s encrypted directory; original files, links and history are not deleted. Subsequent core settings cannot overwrite this configuration. Uncertain saves require a reload, not a retry.';
        const connections = document.createElement('p'); connections.className = 'fieldDescription';
        help.append(text, connections); host.append(help);
        function verify() { if (!active || identity() !== account) { hide(); throw new Error('Native account changed. Reopen configuration.'); } }
        function clearSecrets() { for (const { input, type } of fields.values()) if (type === 'password') input.value = ''; }
        function fill(data) {
            revision = data.Revision; migration = data.MigrationReviewRequired;
            for (const [key, { input, type }] of fields) input.value = type === 'password' ? '' : key.startsWith('Remove') ? 'false' : type === 'ids' ? data[key].join(', ') : String(data[key]);
            dirty = false; uncertain = false; message.textContent = migration ? 'Review retained legacy settings. Save explicitly imports this reviewed configuration; it does not delete the originals.' : 'Saved adapter settings loaded. Secrets are never returned.';
            connections.textContent = data.PublicJellyfinUrl ? 'Developer Portal OAuth redirect: ' + data.PublicJellyfinUrl.replace(/\/$/, '') + '/MediaHub/Link/OAuth/Callback. Activity URL mapping target: ' + data.PublicJellyfinUrl.replace(/^https:\/\//, '').replace(/\/$/, '') + '/MediaHub/Activity/. Use only a trusted existing HTTPS deployment. Loopback origins are for isolated acceptance only.' : 'Save the existing public HTTPS Jellyfin address in the core configuration before enabling Discord or Activity.';
        }
        async function load() {
            const run = ++version; busy = true;
            try { const data = await api(); verify(); if (run === version) fill(data); }
            catch (error) { if (run === version && active) { uncertain = true; message.textContent = errorText(error); if ([401, 403].includes(error.status)) hide(); } }
            finally { if (run === version) busy = false; }
        }
        form.addEventListener('input', () => { dirty = true; }); form.addEventListener('change', () => { dirty = true; });
        form.addEventListener('submit', async event => {
            event.preventDefault(); if (!active || busy || uncertain || revision === undefined) return;
            if (!window.confirm(migration ? 'Import these reviewed Discord settings into the separate plugin? Original state will be retained.' : 'Save these Discord settings? The bot may reconnect; Jellyfin will not restart.')) return;
            const run = version; busy = true;
            try {
                verify(); const body = { Revision: revision, ImportLegacy: migration };
                for (const [key, { input, type }] of fields) { if (key.startsWith('Remove')) continue; body[key] = type === 'bool' ? input.value === 'true' : type === 'ids' ? input.value.split(',').map(v => v.trim()).filter(Boolean) : type === 'number' ? Number(input.value) : type === 'password' ? input.value || null : input.value.trim(); }
                if (fields.get('RemoveBotToken').input.value === 'true') body.BotToken = '';
                if (fields.get('RemoveClientSecret').input.value === 'true') body.ClientSecret = '';
                const data = await api(body); verify(); if (run === version) fill(data);
            } catch (error) { if (run === version && active) { uncertain = true; message.textContent = errorText(error) + ' Reload current state before another Save. No retry was sent.'; clearSecrets(); if ([401, 403].includes(error.status)) hide(); } }
            finally { if (run === version) busy = false; }
        });
        view.querySelector('[data-discord-reload]').addEventListener('click', () => { if (!busy && (!dirty || window.confirm('Discard this unsaved draft and reload?'))) void load(); });
        function hide() { window.removeEventListener('pagehide', hide); active = false; version++; busy = false; uncertain = true; revision = undefined; account = undefined; form.reset(); clearSecrets(); message.textContent = ''; connections.textContent = ''; host.hidden = true; }
        view.addEventListener('viewhide', hide);
        return { show() { window.addEventListener('pagehide', hide); account = identity(); active = true; host.hidden = false; void load(); }, hide };
    };
    window.JellyfinDiscordSettings = create;
    if (typeof define === 'function') define(['loading', 'toast'], function () {
        return function (view) {
            const url = () => ApiClient.getUrl('MediaHub/Discord/Configuration');
            const ui = create(view, body => ApiClient.ajax({ type: body ? 'POST' : 'GET', url: url(), dataType: 'json', ...(body ? { data: JSON.stringify(body), contentType: 'application/json' } : {}) }), () => ApiClient.getCurrentUserId());
            view.addEventListener('viewshow', () => ui.show());
        };
    });
})();
