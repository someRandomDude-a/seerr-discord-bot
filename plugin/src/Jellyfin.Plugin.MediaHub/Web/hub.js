/* global ApiClient */
(function () {
    'use strict';
    window.JellyfinMediaHub = function (view, params) {
        const notice = view.querySelector('[data-notice]');
        const personalSection = view.querySelector('[data-personal]');
        const preferencesForm = view.querySelector('[data-preferences]');
        const personalNotice = view.querySelector('[data-personal-notice]');
        const watchlist = view.querySelector('[data-watchlist]');
        const section = ['deletions', 'watchlist', 'notifications', 'preferences'].includes(params?.hubSection) ? params.hubSection : 'all';
        function applySection() {
            if (section === 'all') return;
            view.querySelector('h1').textContent = section === 'deletions' ? 'Deletion queue' : section === 'preferences' ? 'Your notification preferences' : section === 'watchlist' ? 'Your watchlist' : 'Notifications';
            for (const node of view.querySelectorAll('section')) {
                if (section === 'deletions' ? node.hasAttribute('data-deletions') : node === personalSection || section === 'notifications' && node.hasAttribute('data-deletions')) continue;
                node.hidden = true;
            }
            if (section === 'deletions') return;
            preferencesForm.hidden = section !== 'preferences'; view.querySelector('[data-follow]').hidden = true;
            view.querySelector('[data-preferences-page]').hidden = section !== 'preferences';
            view.querySelector('[data-watchlist-page]').hidden = section !== 'watchlist';
            view.querySelector('[data-watchlist-page] h2').hidden = section === 'watchlist';
            view.querySelector('[data-notifications-page]').hidden = section !== 'notifications';
            view.querySelector('[data-propose]').hidden = true;
        }
        let generation = 0, timer;
        let account, personalState, personalBusy = false, preferencesDirty = false, preferencesRevision, watchSignature, draftVersion = 0;
        let refreshBusy = false;
        let visible = false, integrationAvailable = false;
        const normalize = value => String(value || '').replaceAll('-', '').toLowerCase();
        const status = text => { notice.textContent = text; };
        const configuration = window.JellyfinMediaSettings?.(view, api, checkAccount);
        const clearSettings = () => { configuration?.hide(); view.querySelector('[data-settings]').hidden = true; };
        view.addEventListener('mediahubsettingshidden', () => { view.querySelectorAll('[data-admin-audit]').forEach(node => node.remove()); });
        const messenger = window.JellyfinMediaMessenger?.(view, api);
        const watchParty = window.JellyfinMediaWatchParty?.(view, api, checkAccount);
        const monitoring = window.JellyfinDeletionMonitoring?.(view, api, checkAccount);
        const permissions = window.JellyfinDeletionPermissions?.(view, api, checkAccount);
        for (const button of view.querySelectorAll('[data-open-notification-preferences]')) button.addEventListener('click', () => {
            checkAccount(); const target = '#/userpluginsettings.html?pageUrl=' + encodeURIComponent('/web/ConfigurationPage?name=mediahub') + '&hubSection=preferences';
            if (window.MediaHubOpenNative) void window.MediaHubOpenNative(window.MediaHubNativeUrl + target); else window.location.hash = target;
        });
        async function applyIntegration(data) {
            // Older clients/test responses omit this field; current standalone servers send false.
            const previous = integrationAvailable;
            integrationAvailable = data.DiscordIntegration?.Installed !== false;
            view.querySelector('[data-link-section]').hidden = !integrationAvailable || section !== 'all';
            view.querySelector('[data-native-players]').hidden = !integrationAvailable || Boolean(window.MediaHubNativeUrl) || section !== 'all';
            view.querySelector('[data-sync-activity-theme]').hidden = !integrationAvailable || Boolean(window.MediaHubNativeUrl);
            preferencesForm.elements.discord.closest('.checkboxContainer').hidden = !integrationAvailable;
            if (integrationAvailable && section === 'all') { if (!previous) watchParty?.show(); await watchParty?.refresh(); }
            else { watchParty?.hide(); view.querySelector('[data-watchparty]').hidden = true; messenger?.hide(); }
        }
        const clearPersonal = () => {
            personalSection.hidden = true; preferencesForm.reset(); view.querySelector('[data-follow]').reset();
             watchlist.replaceChildren(); personalNotice.textContent = ''; personalState = undefined;
             view.querySelector('[data-notification-feed]').replaceChildren(); view.querySelector('[data-sync-status]').textContent = '';
             view.querySelector('[data-sync-review]').replaceChildren();
             view.querySelector('[data-notification-diagnostic-result]').textContent = '';
            personalBusy = false; preferencesDirty = false; preferencesRevision = undefined; watchSignature = undefined;
        };
        function hide() {
            visible = false; generation += 1; clearInterval(timer);
            integrationAvailable = false;
            clearSettings(); clearPersonal();
            clearDeletions();
            clearCatalog();
            view.querySelector('[data-native-players]').hidden = true; view.querySelector('[data-player-history]').replaceChildren();
            messenger?.hide();
            watchParty?.hide();
            monitoring?.hide();
            permissions?.hide();
            view.querySelector('[data-handoff-controls]').replaceChildren(); view.querySelector('[data-handoff-notice]').textContent = '';
        }
        const expandedConsentHelp = new Set();
        function clearDeletions() {
            expandedConsentHelp.clear();
            view.querySelector('[data-deletions]').hidden = true;
            view.querySelector('[data-deletion-list]').replaceChildren(); view.querySelector('[data-consent-list]').replaceChildren();
            view.querySelector('[data-propose]').reset();
        }
        function queueButton(label, path, body) {
            const button = document.createElement('button', 'emby-button'); button.type = 'button'; button.className = 'raised emby-button'; button.setAttribute('is', 'emby-button'); button.textContent = label;
            button.addEventListener('click', () => void perform(button, async () => { await api(path, body); status('Saved.'); await refresh(); }));
            return button;
        }
        let catalogVersion = 0;
        const artworkUrls = new Set();
        function clearArtwork() { artworkUrls.forEach(url => window.URL.revokeObjectURL(url)); artworkUrls.clear(); }
        const catalogForm = view.querySelector('[data-catalog-query]');
        function clearCatalog() {
            catalogVersion += 1;
            clearArtwork();
            for (const selector of ['[data-catalog-list]', '[data-catalog-paging]', '[data-media-details]', '[data-storage-results]', '[data-request-permissions-result]']) view.querySelector(selector).replaceChildren();
            view.querySelector('[data-catalog-notice]').textContent = ''; catalogForm.reset();
        }
        function actionButton(label, action) {
            const button = document.createElement('button', 'emby-button'); button.type = 'button'; button.setAttribute('is', 'emby-button'); button.className = 'raised emby-button'; button.textContent = label;
            button.addEventListener('click', () => void perform(button, action)); return button;
        }
        function nativeSelect(container, label, id) {
            const attributes = document.createElement('select'); attributes.setAttribute('label', label); if (id) attributes.id = id;
            container.insertAdjacentHTML('beforeend', attributes.outerHTML.replace('<select', '<select is="emby-select"'));
            return container.querySelector('select');
        }
        async function loadArtwork(card, container, run) {
            const path = `Catalog/${card.Kind}/${card.ExternalId}/Poster`, requestGeneration = generation;
            checkAccount(); let blob;
            if (typeof ApiClient.mediaHubTransfer === 'function') blob = await ApiClient.mediaHubTransfer(path);
            else {
                const url = new URL(ApiClient.getUrl('MediaHub/' + path), window.location.href), token = ApiClient.accessToken?.();
                if (url.origin !== window.location.origin || !token) throw new Error('Artwork requires the current signed-in server.');
                const response = await window.fetch(url, { headers: { Authorization: 'MediaBrowser Token="' + encodeURIComponent(token) + '"' }, credentials: 'same-origin', cache: 'no-store', redirect: 'error' });
                if (!response.ok) throw new Error('Poster unavailable.'); blob = await response.blob();
            }
            await api('Status'); checkAccount();
            if (requestGeneration !== generation || !container.isConnected) return;
            if (!['image/jpeg', 'image/png'].includes(blob.type) || blob.size > 2 * 1024 * 1024) throw new Error('Poster format could not be verified.');
            for (const image of container.querySelectorAll('img')) if (artworkUrls.delete(image.src)) window.URL.revokeObjectURL(image.src);
            if (artworkUrls.size >= 24) throw new Error('Artwork memory limit reached. Browse another page before loading more.');
            const url = window.URL.createObjectURL(blob); artworkUrls.add(url); const image = document.createElement('img'); image.src = url; image.alt = 'Poster for ' + card.Title; image.className = 'cardImage'; image.loading = 'lazy';
            container.replaceChildren(image);
        }
        async function handoffLink() {
            if (window.MediaHubNativeUrl) return;
            const code = params?.hubLink || window.sessionStorage?.getItem('mediahub.handoff');
            if (!code || !/^[0-9a-f]{48}$/i.test(code)) return;
            if (params) delete params.hubLink;
            window.sessionStorage.setItem('mediahub.handoff', code); window.sessionStorage.removeItem('mediahub.resume');
            const url = new URL(window.location.href); url.hash = url.hash.replace(/&hubLink=[^&]*/g, ''); window.history.replaceState(null, '', url);
            const result = await api('Link/Handoff/Status', { Code: code }); const text = view.querySelector('[data-handoff-notice]'), controls = view.querySelector('[data-handoff-controls]'); controls.replaceChildren();
            text.textContent = `Link Discord ${result.Discord} to Jellyfin ${result.Jellyfin}. Only continue if you requested this link.`;
            if (result.State === 'proved' || result.State === 'used') controls.append(actionButton('Confirm these accounts', async () => { await api('Link/Handoff/Complete', { Code: code }); window.sessionStorage.removeItem('mediahub.handoff'); controls.replaceChildren(); text.textContent = 'Accounts linked.'; await refresh(); }));
            else controls.append(actionButton('Verify Discord in this browser', async () => { const started = await api('Link/Handoff/Begin', { Code: code }); const destination = new URL(started.AuthorizeUrl); if (destination.origin !== 'https://discord.com') throw new Error('Invalid identity destination.'); window.location.assign(destination.href); }));
        }
        const activityWarning = data => data?.Activity?.JellyfinUpdated === false ? data.Activity.LocalRecorded === false ? ' Activity update could not be verified.' : ' Activity saved locally; Jellyfin timestamp update unavailable.' : '';
        function showRequestReceipt(container, base, id, run, receipt) {
            if (run !== catalogVersion) return;
            const path = base + '/' + encodeURIComponent(id), descriptions = { prepared: 'Not sent. The prepared plan can still be cancelled.', sending: 'Send is in progress or its result is not confirmed. Do not create a replacement request.', uncertain: 'Outcome not confirmed. Inspect the upstream request before any further action; no automatic resend.', completed: 'Request sent. Fulfilment remains with the media service.', cancelled: 'Unsent plan cancelled.', expired: 'Unsent plan expired.' };
            const p = document.createElement('p'); p.textContent = receipt ? (descriptions[receipt.State] || 'Request requires review.') + activityWarning(receipt) : 'Confirming this request. A connection failure is not proof it was unsent.';
            container.replaceChildren(p, actionButton('Refresh request receipt', async () => { const current = await api(path + '/Receipt'); showRequestReceipt(container, base, id, run, current); }));
            if (receipt?.State === 'prepared') container.append(actionButton('Cancel unsent request plan', async () => { const current = await api(path + '/Cancel', {}); showRequestReceipt(container, base, id, run, current); }));
        }
        async function mediaDetails(card) {
            const run = ++catalogVersion;
            let details = card;
            if (card.NativeOnly && card.NativeId) details = await api('Items/' + card.NativeId + '/Details', {});
            else if (card.ForeignId) details = await api(`Extras/${card.Kind}/${encodeURIComponent(card.ForeignId)}/Details`, {});
            else if (['movie', 'tv'].includes(card.Kind) && card.ExternalId > 0) details = await api(`Catalog/${card.Kind}/${card.ExternalId}/Details`, {});
            else if (card.NativeId) details = { ...card, Activity: await api('Items/' + card.NativeId + '/Activity', { Action: 0 }) };
            if (run !== catalogVersion) return;
            const panel = view.querySelector('[data-media-details]'); panel.replaceChildren();
            const title = document.createElement('h3'); title.className = 'sectionTitle'; title.textContent = details.Title;
            const text = document.createElement('p'); text.textContent = details.Overview;
            const label = document.createElement('p'); label.textContent = details.Status + activityWarning(details); panel.append(title, text, label);
            if (details.Poster && ['movie', 'tv'].includes(details.Kind) && details.ExternalId > 0) {
                const image = document.createElement('div'); panel.append(image, actionButton('Load title artwork', () => loadArtwork(details, image, run)));
            }
            if (!details.NativeId && ['movie', 'tv'].includes(details.Kind) && details.ExternalId > 0) panel.append(actionButton('Choose an accessible Jellyfin edition', async () => {
                const editions = await api(`Catalog/${details.Kind}/${details.ExternalId}/Editions`); if (run !== catalogVersion) return;
                panel.querySelectorAll('[data-edition-choices]').forEach(node => node.remove()); const choices = document.createElement('div'); choices.dataset.editionChoices = ''; panel.append(choices);
                const container = document.createElement('div'); container.className = 'selectContainer'; const select = nativeSelect(container, 'Jellyfin edition', 'hubNativeEdition');
                if (window.MediaHubNativeUrl) { const label = document.createElement('label'); label.className = 'selectLabel'; label.htmlFor = select.id; label.textContent = 'Jellyfin edition'; container.append(label); }
                for (const [index, edition] of editions.entries()) { const option = document.createElement('option'); option.value = edition.Id; option.textContent = `${edition.Title} · edition ${index + 1}`; select.append(option); }
                choices.append(container);
                if (!editions.length) { container.textContent = 'No currently accessible native editions.'; return; }
                choices.append(actionButton('Open selected edition in Jellyfin', async () => {
                    const opened = await api(`Catalog/${details.Kind}/${details.ExternalId}/Editions/${encodeURIComponent(select.value)}/Open`, {}); if (run !== catalogVersion) return;
                    const destination = (window.MediaHubNativeUrl || '') + '#/details?' + new URLSearchParams({ id: normalize(opened.Item), serverId: ApiClient.serverId?.() || '' });
                    if (window.MediaHubOpenNative) await window.MediaHubOpenNative(destination); else window.location.hash = destination;
                }));
            }));
            if (details.NativeId) {
                if (!window.MediaHubNativeUrl) panel.append(actionButton('Choose one of your real Jellyfin players', async () => {
                    const destinations = await api('Players'); if (run !== catalogVersion) return;
                    const choices = document.createElement('div'); choices.className = 'selectContainer'; const select = nativeSelect(choices, 'Your active native players');
                    for (const destination of destinations) { const o = document.createElement('option'); o.value = destination.Id; o.textContent = destination.Name + ' · ' + destination.Client; select.append(o); }
                    panel.append(choices);
                    if (!destinations.length) { choices.textContent = 'No current player on your account supports native media control. Open a real Jellyfin player first.'; return; }
                    choices.append(actionButton('Review play command for this player', async () => {
                        const plan = await api('Players/Prepare', { Target: select.value, Item: details.NativeId }); if (run !== catalogVersion) return;
                        const preview = document.createElement('div'), text = document.createElement('p'); text.textContent = `Play ${plan.Title} now on ${plan.Device}? This may replace its current playback. Preview expires in two minutes.`;
                        function receipt(current) {
                            preview.replaceChildren(); const p = document.createElement('p'); p.textContent = `Native command: ${current?.State || 'not confirmed'}. Acceptance is not playback confirmation. ${current?.ObservedPlayingItem === true ? 'That player currently reports this item; this does not prove this command caused it.' : ''}`;
                            preview.append(p, actionButton('Refresh player receipt · never resend', async () => { const latest = await api('Players/' + plan.Id + '/Receipt'); if (run === catalogVersion) receipt(latest); }));
                            if (['sending', 'uncertain'].includes(current?.State)) preview.append(actionButton('Acknowledge manual inspection · no retry', async () => { if (!window.confirm('I inspected the real player. Record this receipt as reviewed without sending again? A later new command may still duplicate an earlier effect.')) return; const reviewed = await api('Players/' + plan.Id + '/Acknowledge', {}); if (run === catalogVersion) receipt(reviewed); }));
                        }
                        preview.append(text, actionButton('Confirm native play command', async () => { receipt(); const result = await api('Players/' + plan.Id + '/Confirm', {}); if (run === catalogVersion) receipt(result); }), actionButton('Cancel unsent player preview', async () => { const result = await api('Players/' + plan.Id + '/Cancel', {}); if (run === catalogVersion) receipt(result); })); panel.append(preview);
                    }));
                }));
                panel.append(actionButton('Open in Jellyfin', async () => {
                    let id = details.NativeId, opened;
                    if (!details.NativeOnly && ['movie', 'tv'].includes(details.Kind) && details.ExternalId > 0) {
                        opened = await api(`Catalog/${details.Kind}/${details.ExternalId}/Open`, {}); id = opened.Item;
                    }
                    else opened = { Activity: await api('Items/' + id + '/Activity', { Action: 1 }) };
                    if (activityWarning(opened)) {
                        label.textContent = activityWarning(opened).trim();
                        const link = document.createElement('a'); link.textContent = 'Continue to Jellyfin'; link.href = (window.MediaHubNativeUrl || '') + '#/details?' + new URLSearchParams({ id: normalize(id), serverId: ApiClient.serverId?.() || '' });
                        if (window.MediaHubOpenNative) link.addEventListener('click', event => { event.preventDefault(); void perform(link, () => window.MediaHubOpenNative(link.href)); }); panel.append(link); return;
                    }
                    const params = new URLSearchParams({ id: normalize(id), serverId: ApiClient.serverId?.() || '' });
                    if (window.MediaHubNativeUrl) { const destination = new URL(window.MediaHubNativeUrl); if (destination.protocol !== 'https:' || !window.MediaHubOpenNative) throw new Error('Invalid native destination.'); destination.hash = '#/details?' + params; await window.MediaHubOpenNative(destination.href); }
                    else window.location.hash = '#/details?' + params;
                }));
                if (!details.ForeignId) panel.append(actionButton('Add to watchlist', () => mutatePersonal('Me/Watchlist/' + details.NativeId + '/Add', {})), actionButton('Save and subscribe', () => mutatePersonal('Me/Watchlist/' + details.NativeId + '/Bell', { Subscribed: true })));
            }
            if (details.ForeignId) {
                panel.append(actionButton('Add to watchlist', () => mutatePersonal(`Me/ExtraWatchlist/${details.Kind}/${encodeURIComponent(details.ForeignId)}`, { Subscribed: false, SaveOnly: true })));
                panel.append(actionButton('Save and subscribe', () => mutatePersonal(`Me/ExtraWatchlist/${details.Kind}/${encodeURIComponent(details.ForeignId)}`, { Subscribed: true })));
                const container = document.createElement('div'); container.className = 'selectContainer'; const edition = nativeSelect(container, 'Book edition', 'hubBookEdition'); edition.setAttribute('aria-label', 'Book edition');
                for (const id of details.Editions || []) { const option = document.createElement('option'); option.value = id; option.textContent = 'Edition ' + id; edition.append(option); }
                if (details.Kind === 'book') {
                    if (window.MediaHubNativeUrl) { edition.id = 'hubBookEdition'; edition.classList.add('emby-select'); const label = document.createElement('label'); label.className = 'selectLabel'; label.htmlFor = edition.id; label.textContent = 'Book edition'; container.append(label); }
                    panel.append(container);
                }
                panel.append(actionButton('Prepare request', async () => {
                    const selected = edition.value || null;
                    const plan = await api('Extras/Requests/Prepare', { Kind: details.Kind, ForeignId: details.ForeignId, Edition: selected }); if (run !== catalogVersion) return;
                    const confirmation = document.createElement('div'); const summary = document.createElement('p'); summary.textContent = `Confirm request: ${plan.Title}${selected ? ' · Edition ' + selected : ''}`;
                    confirmation.append(summary, actionButton('Confirm request', async () => { showRequestReceipt(confirmation, 'Extras/Requests', plan.Id, run); const result = await api('Extras/Requests/' + encodeURIComponent(plan.Id) + '/Confirm', {}); showRequestReceipt(confirmation, 'Extras/Requests', plan.Id, run, result); }),
                        actionButton('Cancel request plan', async () => { await api('Extras/Requests/' + encodeURIComponent(plan.Id) + '/Cancel', {}); if (run === catalogVersion) confirmation.remove(); })); panel.append(confirmation);
                })); return;
            }
            if (!['movie', 'tv'].includes(details.Kind) || details.ExternalId <= 0) return;
            if (!details.NativeId) panel.append(actionButton('Add to watchlist', () => mutatePersonal(`Me/Watchlist/${details.Kind}/${details.ExternalId}`, { Subscribed: false, SaveOnly: true })), actionButton('Save and subscribe', () => mutatePersonal(`Me/Watchlist/${details.Kind}/${details.ExternalId}`, { Subscribed: true })));
            function checkbox(text) {
                const container = document.createElement('div'); container.className = 'checkboxContainer';
                const label = document.createElement('label'); label.className = 'emby-checkbox-label';
                const input = document.createElement('input', 'emby-checkbox'); input.type = 'checkbox'; input.setAttribute('is', 'emby-checkbox');
                const caption = document.createElement('span'); caption.textContent = text; label.append(input, caption); container.append(label); panel.append(container); return input;
            }
            const fourK = checkbox('Request 4K');
            const seasons = [];
            if (details.Kind === 'tv') {
                const legend = document.createElement('p'); legend.textContent = 'Select seasons, or leave unchecked to request all seasons.'; panel.append(legend);
                for (const number of details.Seasons || []) {
                    const input = checkbox('Season ' + number); seasons.push({ input, number });
                }
            }
            panel.append(actionButton('Prepare request', async () => {
                const selected = seasons.filter(s => s.input.checked).map(s => s.number), is4k = fourK.checked;
                const plan = await api('Requests/Prepare', { Kind: details.Kind, ExternalId: details.ExternalId, Is4k: is4k, Seasons: selected.length ? selected : null });
                if (run !== catalogVersion) return;
                panel.querySelectorAll('[data-request-confirmation]').forEach(node => node.remove());
                const confirmation = document.createElement('div'); confirmation.dataset.requestConfirmation = '';
                const summary = document.createElement('p'); summary.textContent = `Confirm request: ${plan.Title} · ${is4k ? '4K' : 'Standard'}${details.Kind === 'tv' ? ' · ' + (selected.length ? 'Seasons ' + selected.join(', ') : 'All seasons') : ''}`;
                const confirm = actionButton('Confirm request', async () => {
                    showRequestReceipt(confirmation, 'Requests', plan.Id, run);
                    const receipt = await api('Requests/' + encodeURIComponent(plan.Id) + '/Confirm', {});
                    showRequestReceipt(confirmation, 'Requests', plan.Id, run, receipt);
                });
                const cancel = actionButton('Cancel request plan', async () => { await api('Requests/' + encodeURIComponent(plan.Id) + '/Cancel', {}); if (run === catalogVersion) confirmation.replaceChildren(); });
                confirmation.append(summary, confirm, cancel); panel.append(confirmation);
            }));
        }
        async function loadCatalog(page, selection) {
            const run = ++catalogVersion;
            const params = new URLSearchParams({ kind: selection.kind, query: selection.query, page: String(page) });
            const path = selection.source === 'requests' || selection.source === 'allRequests' ? 'Requests?all=' + (selection.source === 'allRequests') + '&page=' + page : selection.source === 'browse' && ['music', 'book'].includes(selection.kind) ? `Extras/${selection.kind}/Search?query=${encodeURIComponent(selection.query)}&page=${page}` : 'Catalog/' + (selection.source === 'library' ? 'Library' : 'Browse') + '?' + params;
            const result = await api(path);
            if (run !== catalogVersion) return;
            const list = view.querySelector('[data-catalog-list]'); clearArtwork(); list.replaceChildren(); view.querySelector('[data-media-details]').replaceChildren();
            view.querySelector('[data-catalog-notice]').textContent = result.Items.length ? 'Page ' + result.Page + ' of ' + result.Pages : 'No results.';
            for (const card of result.Items) {
                const row = document.createElement('li'); row.className = 'listItem'; const body = document.createElement('div'); body.className = 'listItemBody listItemBodyText'; const title = document.createElement('h3'); title.className = 'sectionTitle'; title.textContent = card.Title;
                const state = document.createElement('p'); state.textContent = card.Status + (card.Is4k ? ' · 4K' : ''); const overview = document.createElement('p'); overview.className = 'fieldDescription'; overview.textContent = String(card.Overview || '').slice(0, 300); body.append(title, state, overview);
                row.append(body, actionButton('Details', () => mediaDetails(card)));
                if (card.Poster && ['movie', 'tv'].includes(card.Kind) && card.ExternalId > 0) {
                    const image = document.createElement('div'); row.append(image, actionButton('Load poster', () => loadArtwork(card, image, run)));
                } list.append(row);
            }
            const paging = view.querySelector('[data-catalog-paging]'); paging.replaceChildren();
            if (result.Page > 1) paging.append(actionButton('Previous', () => loadCatalog(result.Page - 1, selection)));
            if (result.Page < result.Pages) paging.append(actionButton('Next', () => loadCatalog(result.Page + 1, selection)));
        }
        catalogForm.addEventListener('submit', event => {
            event.preventDefault();
            const selection = { source: catalogForm.elements.source.value, kind: catalogForm.elements.kind.value, query: catalogForm.elements.query.value.trim() };
            void perform(catalogForm.querySelector('button'), () => loadCatalog(1, selection));
        });
        view.querySelector('[data-storage]').addEventListener('click', event => void perform(event.currentTarget, async () => {
            const rows = await api('Storage'); const panel = view.querySelector('[data-storage-results]'); panel.replaceChildren();
            for (const row of rows) { const text = document.createElement('p'); text.textContent = `${row.Service} #${row.Instance} · ${row.Verified ? (row.Free / 1073741824).toFixed(1) + ' / ' + (row.Total / 1073741824).toFixed(1) + ' GiB free' : 'Not verified'}`; panel.append(text); }
        }));
        view.querySelector('[data-request-permissions]').addEventListener('click', event => void perform(event.currentTarget, async () => {
            const data = await api('Me/RequestPermissions'), panel = view.querySelector('[data-request-permissions-result]'); panel.replaceChildren();
            for (const [label, allowed, fourK, quota] of [['Movies', data.Movies, data.Movie4k, data.MovieQuota], ['Series', data.Series, data.Series4k, data.SeriesQuota]]) {
                const p = document.createElement('p'); p.className = 'fieldDescription';
                const limit = quota?.Limit == null ? 'Limit not supplied by Seerr' : `${quota.Remaining ?? '?'} remaining of ${quota.Limit} per ${quota.Days ?? '?'} days`;
                p.textContent = `${label}: ${allowed ? 'requests allowed' : 'requests not allowed'} · ${fourK ? '4K allowed' : '4K not allowed'} · ${limit}. Permissions are rechecked when requesting.`; panel.append(p);
            }
        }));
        view.querySelector('[data-open-seerr]').addEventListener('click', event => void perform(event.currentTarget, async () => {
            const data = await api('SeerrLink'), url = new URL(data.Url);
            if (!['https:', 'http:'].includes(url.protocol) || url.username || url.password || url.search || url.hash) throw new Error('The saved Seerr destination could not be verified.');
            if (window.MediaHubNativeUrl && url.protocol !== 'https:') throw new Error('Discord external navigation requires an existing trusted HTTPS Seerr address. No credentials or HTTP proxy were exposed.');
            const panel = view.querySelector('[data-request-permissions-result]'), link = document.createElement('a'); link.textContent = 'Continue to Seerr with your own account'; link.href = url.href; link.target = '_blank'; link.rel = 'noopener noreferrer';
            link.addEventListener('click', event => { try { checkAccount(); if (window.MediaHubOpenNative) { event.preventDefault(); void perform(link, () => window.MediaHubOpenNative(link.href)); } } catch { event.preventDefault(); } }); panel.replaceChildren(link);
        }));
        function renderQueue(rows) {
            const list = view.querySelector('[data-deletion-list]'); list.replaceChildren();
            if (!rows.length) { const empty = document.createElement('li'); empty.className = 'listItem'; empty.textContent = 'No active deletion proposals for your accessible items. Open a title in Jellyfin to request deletion without entering IDs.'; list.append(empty); }
            for (const row of rows) {
                const item = document.createElement('li'); item.className = 'listItem';
                const state = { waiting: 'Waiting for consent', queued: 'Queued', ready: row.AutomaticDeletion ? 'Ready' : 'Ready · automatic removal off', blocked: 'Blocked', executing: 'Removing', checking: 'Confirming removal', cleanup: 'Unmonitoring', uncertain: 'Check required' }[row.State] || row.State;
                const body = document.createElement('div'); body.className = 'listItemBody listItemBodyText'; body.textContent = `${row.Title} · ${state} · ${row.Owner}${row.DueAt ? ' · ' + new Date(row.DueAt).toLocaleString() : ''} `; item.append(body);
                if (row.CanUndo) item.append(queueButton('Undo', 'Deletions/' + encodeURIComponent(row.Id) + '/Undo', {}));
                const details = document.createElement('div'); details.className = 'fieldDescription';
                item.append(actionButton('Status and your required action', async () => {
                    const data = await api('Deletions/' + encodeURIComponent(row.Id) + '/Status');
                    if (!item.isConnected) return;
                    const actions = { consent_required: 'Please choose Yes or No.', blocked_by_your_choice: 'Your No blocks deletion while your activity remains recent.', review_required: 'Verification or an uncertain outcome requires review. Do not repeat deletion.', automatic_deletion_off: 'The delay elapsed, but automatic removal is off.', wait: 'No action is required from you right now. Checks, consent or the delay are still pending.', none: 'This proposal is closed.' };
                    const text = document.createElement('p'); text.textContent = `${actions[data.RequiredAction] || 'Refresh to review.'} Your consent: ${data.YourConsent}. ${data.ActivityVerified ? 'Your last recorded activity: ' + (data.YourLastActivity ? new Date(data.YourLastActivity).toLocaleString() : 'none recorded') : 'Your activity could not be verified.'} Deadline: ${data.DueAt ? new Date(data.DueAt).toLocaleString() : 'not scheduled'}. Minimum delay: ${data.DelayDays} days. Automatic removal: ${data.AutomaticDeletion ? 'on' : 'off'}.`;
                    details.replaceChildren(text);
                    if (data.Notice && ['required', 'no', 'yes'].includes(data.YourConsent)) for (const approve of [true, false]) details.append(queueButton(approve ? 'Yes' : 'No', 'Me/Consent/' + encodeURIComponent(data.Notice), { Approve: approve }));
                }), details);
                if (configuration?.authorized()) {
                    const auditButton = actionButton('View administrator audit', async () => {
                    const audit = await api('Deletions/' + encodeURIComponent(row.Id) + '/Audit');
                    if (!configuration.authorized()) return;
                    const text = document.createElement('p'); text.dataset.adminAudit = ''; text.className = 'fieldDescription'; text.textContent = `Audit · owner account ${audit.Owner} · Seerr request ${audit.RequestId} · ${audit.State} · revision ${audit.Revision} · created ${new Date(audit.Created).toLocaleString()}`; item.append(text);
                    });
                    auditButton.dataset.adminAudit = ''; item.append(auditButton);
                }
                list.append(item);
            }
            view.querySelector('[data-deletions]').hidden = false;
        }
        function renderConsent(rows) {
            const list = view.querySelector('[data-consent-list]'), focused = document.activeElement;
            const focusKey = list.contains(focused) ? focused.dataset.consentFocusKey : null;
            for (const id of expandedConsentHelp) if (!rows.some(row => row.Id === id)) expandedConsentHelp.delete(id);
            list.replaceChildren();
            for (const row of rows) {
                const item = document.createElement('li'); item.className = 'listItem'; const body = document.createElement('div'); body.className = 'listItemBody';
                const title = document.createElement('h3'); title.className = 'listItemBodyText'; title.textContent = row.Title; title.id = 'mediaHubPageConsent-' + encodeURIComponent(row.Id);
                const prompt = document.createElement('p'); prompt.className = 'listItemBodyText secondary'; prompt.textContent = 'Allow deletion?'; body.append(title, prompt); item.append(body);
                const path = 'Me/Consent/' + encodeURIComponent(row.Id);
                const yes = queueButton('Yes', path, { Approve: true }); yes.classList.add('button-submit');
                yes.dataset.consentFocusKey = row.Id + ':yes';
                const help = document.createElement('button'); help.type = 'button'; help.className = 'paper-icon-button-light'; help.setAttribute('is', 'paper-icon-button-light'); help.setAttribute('aria-label', 'Help about deletion'); help.title = 'Help about deletion'; help.setAttribute('aria-expanded', 'false');
                const icon = document.createElement('span'); icon.className = 'material-icons'; icon.setAttribute('aria-hidden', 'true'); icon.textContent = 'help_outline'; help.append(icon);
                const details = document.createElement('div'); details.className = 'fieldDescription'; details.hidden = !expandedConsentHelp.has(row.Id); details.id = title.id + '-help';
                if (row.Message && row.Message !== 'Allow deletion?') { const custom = document.createElement('p'); custom.textContent = row.Message; details.append(custom); }
                help.dataset.consentFocusKey = row.Id + ':help'; help.setAttribute('aria-controls', details.id); help.setAttribute('aria-expanded', String(!details.hidden));
                for (const text of ['Every current recent viewer must agree or age out of the configured activity window (14 days by default). No blocks deletion while your activity remains recent. New activity invalidates consent.', 'Yes is consent, not immediate deletion. The full configured delay (at least 2 days by default) must pass; existing deadlines never silently shorten.', 'Proposal permissions are server-configured: own requests by default, or explicitly granted accessible items. Only the proposer can Undo. Verified, approved Radarr/Sonarr destinations handle file removal after all checks pass; administrators cannot bypass these checks.', 'Automatic removal is off by default. When off, nothing is automatically deleted. Unverified or uncertain state blocks deletion. Opening this page or Help does not vote, mark updates read, or record activity.']) { const paragraph = document.createElement('p'); paragraph.textContent = text; details.append(paragraph); }
                help.addEventListener('click', () => { details.hidden = !details.hidden; help.setAttribute('aria-expanded', String(!details.hidden)); if (details.hidden) expandedConsentHelp.delete(row.Id); else expandedConsentHelp.add(row.Id); });
                const no = queueButton('No', path, { Approve: false }); no.dataset.consentFocusKey = row.Id + ':no';
                const actions = document.createElement('div'); actions.setAttribute('role', 'group'); actions.setAttribute('aria-label', 'Allow deletion?'); actions.setAttribute('aria-describedby', title.id); actions.append(yes, no, help);
                body.append(actions, details); list.append(item);
            }
            if (focusKey) ([...list.querySelectorAll('[data-consent-focus-key]')].find(node => node.dataset.consentFocusKey === focusKey) || view.querySelector('[data-review-deletions]')).focus();
        }
        const currentAccount = () => normalize(ApiClient.getCurrentUserId()) + '@' + normalize(ApiClient.serverId?.());
        function checkAccount() {
            if (!account || currentAccount() !== account) {
                hide(); status('Account changed. Reopen Media Hub.');
                throw new Error('Account changed. Reopen Media Hub.');
            }
        }
        async function api(path, data) {
            const requestGeneration = generation;
            try {
                checkAccount();
                const result = await ApiClient.ajax({ type: data ? 'POST' : 'GET', url: ApiClient.getUrl('MediaHub/' + path),
                    dataType: 'json', contentType: 'application/json', data: data ? JSON.stringify(data) : undefined });
                if (requestGeneration !== generation) throw new Error('Page changed. Refresh to continue.');
                checkAccount();
                return result;
            } catch (error) {
                const optionalDenied = error.status === 403 && (path === 'Settings' || path === 'WatchParty' || path.startsWith('WatchParty/'));
                if (requestGeneration === generation && !optionalDenied && path !== 'Messaging/Access') {
                    if ([401, 403].includes(error.status)) { hide(); status('Sign in again, then reopen Media Hub.'); }
                }
                const failure = new Error(error.responseJSON?.Error || error.responseJSON?.error || error.message || 'Action not confirmed. Check the result before retrying.'); failure.status = error.status; throw failure;
            }
        }
        async function refresh() {
            if (refreshBusy) return;
            refreshBusy = true;
            const run = generation;
            try {
                await Promise.all([
                    api('Status').then(async data => { const text = view.querySelector('[data-web-dependencies]'); text.hidden = !data.Web || data.Web.RegistrationsReady; text.textContent = data.Web && !data.Web.RegistrationsReady ? 'Web integration unavailable. Install Jellyfin 12.2-compatible File Transformation and Plugin Pages, then restart. See setup for requirements.' : ''; await applyIntegration(data); }), section === 'all' && configuration?.verifyAccess(),
                    api('Me').then(data => applyPersonal(data)),
                     api('Deletions').then(renderQueue), api('Me/Consent').then(renderConsent), api('Me/Notifications').then(renderNotifications), monitoring?.refresh()
                ]);
            } finally { if (run === generation) { refreshBusy = false; applySection(); } }
        }
        async function perform(button, action) {
            if (button.disabled) return;
            const run = generation;
            button.disabled = true; button.setAttribute('aria-busy', 'true');
            try { await action(); } catch (error) { if (run === generation) status(error.message); }
            finally { if (run === generation) { button.disabled = false; button.removeAttribute('aria-busy'); updatePersonalControls(); } }
        }
        function loadPreferences() {
            if (!personalState) return;
            const p = personalState.Preferences;
            preferencesForm.elements.jellyfin.checked = p.JellyfinNotifications;
            preferencesForm.elements.discord.checked = p.DiscordNotifications;
            preferencesForm.elements.muted.checked = p.Muted;
            preferencesForm.elements.ownRequests.checked = p.OwnRequestNotifications !== false;
            const rules = personalState.DeletionRules || { DefaultDelayDays: 2, RecentActivityDays: 14 };
            const delay = preferencesForm.elements.deletionDelay;
            delay.min = rules.DefaultDelayDays; delay.max = rules.RecentActivityDays;
            delay.value = personalState.EffectiveDeletionDelayDays ?? p.DeletionDelayDays ?? rules.DefaultDelayDays;
            if (preferencesForm.elements.deletionMessage) preferencesForm.elements.deletionMessage.value = p.DeletionMessage || '';
            preferencesRevision = personalState.Revision; preferencesDirty = false; personalNotice.textContent = '';
            updatePersonalControls();
        }
        function updatePersonalControls() {
            const conflict = preferencesDirty && personalState && preferencesRevision !== personalState.Revision;
            personalSection.querySelectorAll('button').forEach(button => { button.disabled = personalBusy || !personalState; });
            preferencesForm.querySelectorAll('input').forEach(input => { input.disabled = personalBusy; });
            preferencesForm.querySelector('[type="submit"]').disabled = personalBusy || !personalState || Boolean(conflict);
            if (conflict) personalNotice.textContent = 'Settings changed elsewhere. Your draft was kept. Reload before saving.';
        }
        function applyPersonal(data, savedPreferences = false) {
            // A late poll must not replace a more recent confirmed mutation.
            if (personalState && data.Revision < personalState.Revision) return;
            if (personalState && data.Revision !== personalState.Revision) view.querySelector('[data-sync-review]').replaceChildren();
            personalState = data; personalSection.hidden = false;
            view.querySelector('[data-legacy-review]').hidden = !data.WatchlistReviewRequired;
            if (!preferencesDirty || savedPreferences) loadPreferences();
            const external = data.ExternalWatchlist || [], extras = data.ExtraWatchlist || [];
            const signature = JSON.stringify([data.Watchlist, external, extras]);
            if (signature !== watchSignature) {
                watchSignature = signature; watchlist.replaceChildren();
                 for (const item of data.Watchlist) {
                         if ([...external, ...extras].some(w => w.NativeId && normalize(w.NativeId) === normalize(item.ItemId) || w.NativeIds?.some(id => normalize(id) === normalize(item.ItemId)))) continue;
                     const row = document.createElement('li'); row.className = 'listItem';
                    const title = document.createElement('span'); title.className = 'listItemBody listItemBodyText'; title.textContent = `${item.Title} · ${item.Kind} `; row.append(title);
                    const bell = document.createElement('button', 'emby-button'); bell.setAttribute('is', 'emby-button'); bell.className = 'raised emby-button'; bell.type = 'button';
                    bell.textContent = item.Subscribed ? 'Notifications on' : 'Notifications off'; bell.setAttribute('aria-pressed', String(item.Subscribed));
                    bell.setAttribute('aria-label', `Notifications for ${item.Title}`);
                    bell.addEventListener('click', () => void mutatePersonal('Me/Watchlist/' + encodeURIComponent(item.ItemId) + '/Bell', { Subscribed: !item.Subscribed }));
                    const remove = document.createElement('button', 'emby-button'); remove.setAttribute('is', 'emby-button'); remove.className = 'raised emby-button'; remove.type = 'button'; remove.textContent = 'Remove';
                    remove.setAttribute('aria-label', `Remove ${item.Title} from your watchlist`);
                    remove.addEventListener('click', () => void mutatePersonal('Me/Watchlist/' + encodeURIComponent(item.ItemId) + '/Remove', {}));
                     row.append(bell, document.createTextNode(' '), remove); watchlist.append(row);
                 }
                 for (const item of external) {
                      const row = document.createElement('li'); row.className = 'listItem'; const label = document.createElement('span'); label.className = 'listItemBody listItemBodyText'; label.textContent = `${item.Title} · ${item.Kind} `;
                     const path = `Me/Watchlist/${item.Kind}/${item.ExternalId}`;
                     const bell = actionButton(item.Subscribed ? 'Notifications on' : 'Notifications off', () => mutatePersonal(path, { Subscribed: !item.Subscribed })); bell.setAttribute('aria-pressed', String(item.Subscribed));
                     row.append(label, bell, actionButton('Remove', () => mutatePersonal(path, { Subscribed: null }))); watchlist.append(row);
                 }
                 for (const item of extras) {
                      const row = document.createElement('li'); row.className = 'listItem'; const text = document.createElement('span'); text.className = 'listItemBody listItemBodyText'; text.textContent = `${item.Title} · ${item.Kind} `;
                     const path = `Me/ExtraWatchlist/${item.Kind}/${encodeURIComponent(item.ForeignId)}`;
                     row.append(text, actionButton(item.Subscribed ? 'Notifications on' : 'Notifications off', () => mutatePersonal(path, { Subscribed: !item.Subscribed })), actionButton('Remove', () => mutatePersonal(path, { Subscribed: null }))); watchlist.append(row);
                 }
            }
            view.querySelector('[data-watchlist-empty]').hidden = data.Watchlist.length + external.length + extras.length > 0;
            updatePersonalControls();
            applySection();
        }
        async function mutatePersonal(path, fields, savedPreferences = false) {
            if (personalBusy || !personalState) return false;
            const run = generation;
            const savedDraftVersion = draftVersion;
            const expectedRevision = savedPreferences ? preferencesRevision : personalState.Revision;
            personalBusy = true; updatePersonalControls();
            try {
                const data = await api(path, { ...fields, Revision: expectedRevision });
                applyPersonal(data, savedPreferences && savedDraftVersion === draftVersion); status('Saved.' + activityWarning(data)); return true;
            } catch (error) {
                if (run === generation) {
                    status(error.message);
                    // Read after a conflict/uncertain write, never replay the mutation.
                    try { applyPersonal(await api('Me')); } catch { /* The original error remains visible. */ }
                }
                return false;
            } finally { if (run === generation) { personalBusy = false; updatePersonalControls(); } }
        }
        preferencesForm.addEventListener('change', () => { preferencesDirty = true; draftVersion += 1; updatePersonalControls(); });
        function renderNotifications(rows) {
            const list = view.querySelector('[data-notification-feed]'); list.replaceChildren();
            for (const row of Array.isArray(rows) ? rows : []) {
                const item = document.createElement('li'); item.className = 'listItem'; const body = document.createElement('div'); body.className = 'listItemBody listItemBodyText'; body.textContent = `${row.Title} · ${row.Status} `; item.append(body);
                if (!row.Seen) item.append(actionButton('Mark read', async () => { await api('Me/Notifications/' + encodeURIComponent(row.Id) + '/Seen', {}); await refresh(); })); list.append(item);
            }
        }
        view.querySelector('[data-watch-sync]').addEventListener('click', event => void perform(event.currentTarget, async () => {
            const result = await api('Me/Watchlist/Sync', {});
            view.querySelector('[data-sync-status]').textContent = result.Complete ? 'Synced.' : 'Sync pending. An earlier change is unconfirmed; no automatic resend.';
            await refresh();
        }));
        view.querySelector('[data-legacy-review]').addEventListener('click', event => void perform(event.currentTarget, async () => {
            const review = await api('Me/Watchlist/Legacy/Review', {}), panel = view.querySelector('[data-sync-review]'); panel.replaceChildren();
            const text = document.createElement('p'); text.textContent = 'Review retained titles: ' + (review.Titles.join(', ') || 'No currently saved titles') + '. Approval permits a later explicit union sync. Older uncertain writes are not retried; this approval sends no membership change.';
            panel.append(text, actionButton('Approve this retained watchlist', async () => { await api('Me/Watchlist/Legacy/Accept', { Token: review.Token }); panel.replaceChildren(); await refresh(); }));
        }));
        view.querySelector('[data-sync-issues]').addEventListener('click', event => void perform(event.currentTarget, async () => {
            const rows = await api('Me/Watchlist/Sync/Issues'), panel = view.querySelector('[data-sync-review]'); panel.replaceChildren();
            if (!rows.length) panel.textContent = 'No pending movie/series membership changes.';
            for (const row of rows) {
                const box = document.createElement('div'), p = document.createElement('p'); box.className = 'verticalSection';
                p.textContent = `${row.Title} · ${row.State === 'ready' ? 'Not sent yet' : 'Outcome unconfirmed'} · intended membership: ${row.Desired ? 'saved' : 'removed'}`; box.append(p);
                if (row.State !== 'ready') box.append(actionButton('Inspect current Seerr membership', async () => {
                    const review = await api('Me/Watchlist/Sync/Review', { Kind: row.Kind, Id: row.ExternalId });
                    box.replaceChildren(); const description = document.createElement('p'); description.textContent = `${review.Title} · currently ${review.RemoteMembership ? 'saved in Seerr' : 'not saved in Seerr'}. Accepting this observed state changes only your local watchlist and clears this pending change. It does not resend anything. Review expires in five minutes.`;
                    box.append(description, actionButton('Accept current Seerr membership locally', async () => {
                        if (!window.confirm('Adopt the displayed Seerr membership locally and clear this pending change without resending it?')) return;
                        await api('Me/Watchlist/Sync/AcceptRemote', { Token: review.Token }); panel.replaceChildren(); status('Observed Seerr membership adopted locally. No outbound retry was sent.'); await refresh();
                    }));
                })); panel.append(box);
            }
        }));
        view.querySelector('[data-player-receipts]').addEventListener('click', event => void perform(event.currentTarget, async () => {
            if (window.MediaHubNativeUrl) return;
            const rows = await api('Players/History'), panel = view.querySelector('[data-player-history]'); panel.replaceChildren();
            if (!rows.length) panel.textContent = 'No retained accessible player receipts.';
            for (const row of rows) {
                const box = document.createElement('div'), label = document.createElement('p'); box.className = 'verticalSection'; label.textContent = `${row.Preview.Title} · ${row.Preview.Device} · ${row.Receipt.State}. Command state is not proof of playback.`; box.append(label);
                if (['sending', 'uncertain'].includes(row.Receipt.State)) box.append(actionButton('Acknowledge manual player inspection · no retry', async () => {
                    if (!window.confirm('I inspected the real player. Mark this uncertain command reviewed without resending? A later new command may duplicate an earlier effect.')) return;
                    const receipt = await api('Players/' + row.Preview.Id + '/Acknowledge', {}); label.textContent = `${row.Preview.Title} · ${receipt.State}. No retry sent.`;
                }));
                if (row.Receipt.State === 'prepared') box.append(actionButton('Cancel this unsent preview', async () => { await api('Players/' + row.Preview.Id + '/Cancel', {}); label.textContent = 'Unsent player preview cancelled.'; })); panel.append(box);
            }
        }));
        view.querySelector('[data-notification-diagnostics]').addEventListener('click', event => void perform(event.currentTarget, async () => {
            const data = await api('Me/Notifications/Diagnostics');
            view.querySelector('[data-notification-diagnostic-result]').textContent = `Last full scan in this process: ${data.LastCompletedScan ? new Date(data.LastCompletedScan).toLocaleString() : 'not completed yet'} · Discord opt-in ${data.DiscordOptIn ? 'on' : 'off'} · ${data.Muted ? 'muted' : 'not muted'} · deliveries: ${Object.entries(data.Deliveries).map(([key, value]) => key + ' ' + value).join(', ') || 'none'}. Uncertain sends are not automatically repeated.`;
        }));
        view.querySelector('[data-propose]').addEventListener('submit', event => {
            event.preventDefault(); const form = event.currentTarget;
            const item = normalize(form.elements.mediaId.value.trim());
            if (!/^[0-9a-f]{32}$/.test(item)) { status('Enter a Jellyfin movie or show ID.'); return; }
            void perform(form.querySelector('button'), async () => {
                await api('Deletions', { RequestId: Number(form.elements.requestId.value), Item: item }); form.reset(); status('Deletion proposed.'); await refresh();
            });
        });
        view.querySelector('[data-review-deletions]').addEventListener('click', event => void perform(event.currentTarget, async () => { renderQueue(await api('Deletions/Refresh', {})); }));
        preferencesForm.addEventListener('submit', event => {
            event.preventDefault();
            if (preferencesDirty && preferencesRevision !== personalState?.Revision) return;
            void mutatePersonal('Me', { Preferences: { ...personalState?.Preferences, JellyfinNotifications: preferencesForm.elements.jellyfin.checked,
                DiscordNotifications: preferencesForm.elements.discord.checked, Muted: preferencesForm.elements.muted.checked,
                OwnRequestNotifications: preferencesForm.elements.ownRequests.checked,
                DeletionMessage: preferencesForm.elements.deletionMessage?.value.trim() || null,
                DeletionDelayDays: Number(preferencesForm.elements.deletionDelay.value) } }, true);
        });
        view.querySelector('[data-reload-preferences]').addEventListener('click', loadPreferences);
        view.querySelector('[data-sync-activity-theme]').addEventListener('click', () => {
            if (window.MediaHubNativeUrl || !personalState) return;
            const hex = value => { const match = String(value).match(/^rgba?\((\d+),\s*(\d+),\s*(\d+)(?:,\s*([\d.]+))?\)$/); return match && (match[4] === undefined || Number(match[4]) > 0) ? '#' + match.slice(1, 4).map(n => Math.max(0, Math.min(255, Number(n))).toString(16).padStart(2, '0')).join('') : null; };
            const body = window.getComputedStyle(document.body), page = window.getComputedStyle(view), button = window.getComputedStyle(preferencesForm.querySelector('[type="submit"]'));
            const Theme = { Background: hex(body.backgroundColor) || '#101010', Text: hex(page.color) || '#eeeeee', Accent: hex(button.backgroundColor) || '#00a4dc', Surface: hex(page.backgroundColor) || hex(body.backgroundColor) || '#101010' };
            void mutatePersonal('Me', { Preferences: { ...personalState.Preferences, Theme } });
        });
        view.querySelector('[data-follow]').addEventListener('submit', event => {
            event.preventDefault(); const form = event.currentTarget;
            const item = normalize(form.elements.namedItem('item').value.trim());
            if (!/^[0-9a-f]{32}$/.test(item)) { status('Enter the Jellyfin item ID from its details URL.'); return; }
            const run = generation;
            void mutatePersonal('Me/Watchlist/' + item + '/Bell', { Subscribed: true }).then(saved => { if (saved && run === generation) form.reset(); });
        });
        view.querySelector('[data-link]').addEventListener('submit', event => {
            event.preventDefault(); const form = event.currentTarget;
            void perform(form.querySelector('button'), async () => {
                await api('Link/Approve', { Code: form.elements.code.value.trim(), DiscordId: form.elements.discord.value.trim() });
                form.elements.code.value = ''; status('Discord linked.'); await refresh();
            });
        });
        async function show() {
            if (visible) return;
            visible = true;
             const run = ++generation; refreshBusy = false; clearInterval(timer); clearSettings(); clearPersonal(); clearDeletions(); clearCatalog();
            // Clear controls left busy by a previous page lifetime, not by this one.
            view.querySelectorAll('button').forEach(button => { button.disabled = false; button.removeAttribute('aria-busy'); });
            if (params?.hubTicket || /[?&]hubTicket=/.test(window.location.hash)) {
                // Retired links are scrubbed even if authentication/status fails. Never redeem
                // them, retain them in browser storage or pass them to a future provider.
                if (params) delete params.hubTicket;
                const url = new URL(window.location.href);
                url.hash = url.hash.replace(/([?&])hubTicket=[^&]*/g, '$1').replace(/[?&]$/, '').replace(/&&/g, '&');
                window.history.replaceState(null, '', url);
                status('This older watch-party link is retired. Use Watch together with Private SyncPlay.');
            }
            try {
                account = currentAccount();
                await refresh();
                if (section === 'all' && integrationAvailable) void messenger?.show();
                try { if (section === 'all' && integrationAvailable) await handoffLink(); }
                catch (error) { if (run !== generation) return; view.querySelector('[data-handoff-controls]').replaceChildren(); view.querySelector('[data-handoff-notice]').textContent = error.message; }
                timer = setInterval(() => { if (!document.hidden) void refresh().catch(error => { if (run === generation) status(error.message); }); }, 10000);
                // Do not render/rebuild forms during status updates: preserve drafts.
            } catch (error) { if (run === generation) status(error.message); }
            // Local elevated settings must remain repairable when untrusted old integration state is blocked.
            if (run === generation && section === 'all' && !window.MediaHubNativeUrl) try {
                const config = await api('Settings'); configuration?.ready(config); monitoring?.show(); permissions?.show();
            } catch { if (run === generation) clearSettings(); }
            applySection();
        }
        view.addEventListener('viewshow', show);
        view.addEventListener('viewhide', hide);
    };
})();
