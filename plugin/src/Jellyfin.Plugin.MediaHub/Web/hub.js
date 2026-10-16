/* global ApiClient */
(function () {
    'use strict';
    window.JellyfinMediaHub = function (view, params) {
        const notice = view.querySelector('[data-notice]');
        const partyLabel = view.querySelector('[data-party]');
        const personalSection = view.querySelector('[data-personal]');
        const preferencesForm = view.querySelector('[data-preferences]');
        const personalNotice = view.querySelector('[data-personal-notice]');
        const watchlist = view.querySelector('[data-watchlist]');
        let generation = 0, timer, confirmationTimer, unsubscribe, activeParty, revision, expectedGroup, lastGroup;
        let account, personalState, personalBusy = false, preferencesDirty = false, preferencesRevision, watchSignature, draftVersion = 0;
        let refreshBusy = false, configSnapshot;
        let visible = false;
        const normalize = value => String(value || '').replaceAll('-', '').toLowerCase();
        const status = text => {
            notice.textContent = text;
            if (!view.querySelector('[data-onboarding]').hidden) view.querySelector('[data-tutorial-notice]').textContent = text;
        };
        const tutorial = setupTutorial();
        const privateClear = () => { partyLabel.textContent = ''; view.querySelector('[data-close]').hidden = true; activeParty = null; expectedGroup = null; lastGroup = null; clearTimeout(confirmationTimer); };
        const clearSettings = () => { tutorial.unavailable(); view.querySelector('[data-settings]').hidden = true; view.querySelector('[data-config]').reset(); view.querySelector('[data-features]').reset(); revision = undefined; configSnapshot = undefined; };
        const messenger = window.JellyfinMediaMessenger?.(view, api);
        const clearPersonal = () => {
            personalSection.hidden = true; preferencesForm.reset(); view.querySelector('[data-follow]').reset();
             watchlist.replaceChildren(); personalNotice.textContent = ''; personalState = undefined;
             view.querySelector('[data-notification-feed]').replaceChildren(); view.querySelector('[data-sync-status]').textContent = '';
            personalBusy = false; preferencesDirty = false; preferencesRevision = undefined; watchSignature = undefined;
        };
        function hide() {
            visible = false; generation += 1; clearInterval(timer); unsubscribe?.(); unsubscribe = null;
            privateClear(); clearSettings(); clearPersonal();
            clearDeletions();
            clearCatalog();
            messenger?.hide();
            view.querySelector('[data-handoff-controls]').replaceChildren(); view.querySelector('[data-handoff-notice]').textContent = '';
        }
        function clearDeletions() {
            view.querySelector('[data-deletions]').hidden = true;
            view.querySelector('[data-deletion-list]').replaceChildren(); view.querySelector('[data-consent-list]').replaceChildren();
            view.querySelector('[data-propose]').reset();
        }
        function queueButton(label, path, body) {
            const button = document.createElement('button'); button.type = 'button'; button.className = 'raised emby-button'; button.setAttribute('is', 'emby-button'); button.textContent = label;
            button.addEventListener('click', () => void perform(button, async () => { await api(path, body); status('Saved.'); await refresh(); }));
            return button;
        }
        let catalogVersion = 0;
        const catalogForm = view.querySelector('[data-catalog-query]');
        function clearCatalog() {
            catalogVersion += 1;
            for (const selector of ['[data-catalog-list]', '[data-catalog-paging]', '[data-media-details]', '[data-storage-results]']) view.querySelector(selector).replaceChildren();
            view.querySelector('[data-catalog-notice]').textContent = ''; catalogForm.reset();
        }
        function actionButton(label, action) {
            const button = document.createElement('button'); button.type = 'button'; button.setAttribute('is', 'emby-button'); button.className = 'raised emby-button'; button.textContent = label;
            button.addEventListener('click', () => void perform(button, action)); return button;
        }
        function setupTutorial() {
            const section = view.querySelector('[data-tutorial-section]'), guide = view.querySelector('[data-onboarding]');
            const workspace = view.querySelector('[data-hub-workspace]'), progress = view.querySelector('[data-tutorial-progress]');
            const result = view.querySelector('[data-tutorial-result]'), message = view.querySelector('[data-tutorial-notice]');
            const start = view.querySelector('[data-tutorial-start]'), restart = view.querySelector('[data-tutorial-restart]');
            const back = view.querySelector('[data-tutorial-back]'), next = view.querySelector('[data-tutorial-next]');
            const order = ['page', 'application', 'permissions', 'ids', 'configure', 'intents', 'activity', 'optional-services', 'link', 'troubleshooting'];
            const steps = order.map(id => guide.querySelector('[data-setup-step="' + id + '"]'));
            const forms = { config: view.querySelector('[data-config]'), features: view.querySelector('[data-features]'), link: view.querySelector('[data-link-section]') };
            const homes = Object.fromEntries(Object.keys(forms).map(name => [name, view.querySelector('[data-tutorial-home="' + name + '"]')]));
            const groups = [...view.querySelectorAll('[data-feature-group]')];
            const saved = {}, savedGroups = new Map(); let active = false, index = 0, key, state, config;
            const fingerprint = form => JSON.stringify([...form.querySelectorAll('input,select,textarea')].filter(input => input.name && input.type !== 'password').map(input => [input.name, input.type === 'checkbox' ? input.checked : input.value]));
            const dirty = name => fingerprint(forms[name]) !== saved[name] || [...forms[name].querySelectorAll('input[type="password"]')].some(input => input.value.length > 0);
            const dirtyGroup = group => fingerprint(group) !== savedGroups.get(group) || [...group.querySelectorAll('input[type="password"]')].some(input => input.value.length > 0);
            function remember(completed = false) {
                state = { Version: 1, Step: order[index], Completed: completed };
                // Guidance only: never persist settings, credentials, form drafts or access decisions here.
                try { if (key) window.localStorage.setItem(key, JSON.stringify(state)); } catch { /* Tutorial remains usable with browser storage disabled. */ }
            }
            function read() {
                try {
                    const raw = window.localStorage.getItem(key);
                    if (!raw || raw.length > 256) return null;
                    const value = JSON.parse(raw);
                    return value && Object.keys(value).sort().join(',') === 'Completed,Step,Version' && value.Version === 1 && order.includes(value.Step) && typeof value.Completed === 'boolean' ? value : null;
                } catch { return null; }
            }
            function restore() {
                for (const name of Object.keys(forms)) homes[name].append(forms[name]);
                groups.forEach(group => { group.hidden = false; });
            }
            function allowed() {
                if (!visible || !config || window.MediaHubNativeUrl) return false;
                if (currentAccount() !== account) { hide(); status('Account changed. Reopen Media Hub.'); return false; }
                return true;
            }
            function update() {
                if (!active) return;
                back.disabled = index === 0;
                next.textContent = index === order.length - 1 ? 'Finish tutorial' : 'Next';
                const step = order[index];
                const featureSlot = step === 'intents' ? 'messaging' : step === 'activity' ? 'activity' : step === 'optional-services' ? 'services' : null;
                // Restart/resume must not hide a draft that this shared form's Save button would submit.
                if (featureSlot) groups.forEach(group => { group.hidden = group.dataset.featureGroup !== featureSlot && !dirtyGroup(group); });
                const pending = step === 'configure' ? dirty('config') : ['intents', 'activity', 'optional-services'].includes(step) ? dirty('features') : step === 'troubleshooting' && (dirty('config') || dirty('features'));
                const needsSave = ['configure', 'troubleshooting'].includes(step) && config.Revision === 0;
                next.disabled = pending || needsSave;
                if (pending) message.textContent = 'You have unsaved settings. Use the Save button before continuing. Pausing or restarting the tutorial keeps your draft; neither saves it.';
                else if (needsSave) message.textContent = 'Review and explicitly save the connection settings in this step before continuing. Saving is not a connection test.';
                else if (message.textContent.startsWith('You have unsaved settings.') || message.textContent.startsWith('Review and explicitly save')) message.textContent = '';
                view.querySelector('[data-tutorial-review]').textContent = 'Saved configuration: Discord ' + (config.DiscordEnabled ? 'enabled' : 'off') + '; Activity ' + (config.Features?.ActivityEnabled ? 'enabled' : 'off') + '; automatic deletion ' + (config.AutomaticDeletion ? 'enabled — review before use' : 'off') + '. Confirm the manual identity, permission and read-only checks above. No live connection test is implied.';
            }
            function draw(focus = false) {
                restore(); steps.forEach(step => { step.hidden = true; });
                guide.hidden = !active; workspace.hidden = active;
                start.hidden = active; restart.hidden = false;
                start.textContent = state && !state.Completed ? 'Resume setup tutorial' : 'Start setup tutorial';
                if (!active) return;
                const step = steps[index]; step.hidden = false;
                progress.textContent = 'Step ' + (index + 1) + ' of ' + order.length + ' · ' + step.querySelector('h3').textContent;
                const slot = order[index] === 'configure' ? 'config' : order[index] === 'intents' ? 'messaging' : order[index] === 'activity' ? 'activity' : order[index] === 'optional-services' ? 'services' : order[index] === 'link' ? 'link' : null;
                if (slot) {
                    const name = slot === 'config' || slot === 'link' ? slot : 'features';
                    step.querySelector('[data-tutorial-slot="' + slot + '"]').append(forms[name]);
                    if (name === 'features') groups.forEach(group => { group.hidden = group.dataset.featureGroup !== slot; });
                }
                update(); if (focus) step.querySelector('h3').focus();
            }
            function open(position) {
                index = Math.max(0, Math.min(order.length - 1, position)); active = true;
                result.textContent = ''; message.textContent = ''; remember(); draw(true);
            }
            start.addEventListener('click', () => { if (allowed()) open(state && !state.Completed ? order.indexOf(state.Step) : 0); });
            restart.addEventListener('click', () => { if (allowed()) open(0); });
            back.addEventListener('click', () => { if (allowed() && index > 0) open(index - 1); });
            next.addEventListener('click', () => {
                if (!allowed()) return; update(); if (next.disabled) return;
                if (index < order.length - 1) open(index + 1);
                else { remember(true); active = false; draw(); result.textContent = 'Tutorial complete. You can restart it here at any time. Only explicit Save actions changed settings; connection and permission checks still need to be confirmed.'; start.focus(); }
            });
            view.querySelector('[data-tutorial-pause]').addEventListener('click', () => {
                if (!allowed()) return; remember(); active = false; draw(); result.textContent = 'Tutorial paused. Your settings and drafts were not changed. Resume or restart whenever you are ready.'; start.focus();
            });
            for (const name of ['config', 'features']) for (const event of ['input', 'change']) forms[name].addEventListener(event, update);
            return {
                ready(value) {
                    if (window.MediaHubNativeUrl || !Number.isInteger(value.Revision) || value.Revision < 0) return;
                    config = value; section.hidden = false;
                    key = 'mediahub.setup.v1.' + JSON.stringify([currentAccount(), window.location.pathname]); state = read();
                    for (const name of ['config', 'features']) saved[name] = fingerprint(forms[name]);
                    groups.forEach(group => savedGroups.set(group, fingerprint(group)));
                    if (value.Revision === 0 || value.ConfigurationReviewRequired || state && !state.Completed)
                        open(view.querySelector('[data-handoff-controls]').childElementCount > 0 ? order.indexOf('link') : state && !state.Completed ? order.indexOf(state.Step) : 0);
                    else { active = false; draw(); }
                },
                saved(name, value) { config = value; saved[name] = fingerprint(forms[name]); if (name === 'features') groups.forEach(group => savedGroups.set(group, fingerprint(group))); update(); },
                unavailable() {
                    active = false; restore(); guide.hidden = true; workspace.hidden = false; section.hidden = true;
                    steps.forEach(step => { step.hidden = true; }); result.textContent = ''; message.textContent = ''; progress.textContent = ''; view.querySelector('[data-tutorial-review]').textContent = '';
                    key = undefined; state = undefined; config = undefined;
                    savedGroups.clear(); delete saved.config; delete saved.features;
                }
            };
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
        async function mediaDetails(card) {
            const run = ++catalogVersion;
            let details = card;
            if (card.ForeignId) details = await api(`Extras/${card.Kind}/${encodeURIComponent(card.ForeignId)}/Details`, {});
            else if (['movie', 'tv'].includes(card.Kind) && card.ExternalId > 0) details = await api(`Catalog/${card.Kind}/${card.ExternalId}/Details`, {});
            else if (card.NativeId) details = { ...card, Activity: await api('Items/' + card.NativeId + '/Activity', { Action: 0 }) };
            if (run !== catalogVersion) return;
            const panel = view.querySelector('[data-media-details]'); panel.replaceChildren();
            const title = document.createElement('h3'); title.textContent = details.Title;
            const text = document.createElement('p'); text.textContent = details.Overview;
            const label = document.createElement('p'); label.textContent = details.Status + activityWarning(details); panel.append(title, text, label);
            if (details.NativeId) {
                panel.append(actionButton('Open in Jellyfin', async () => {
                    let id = details.NativeId, opened;
                    if (['movie', 'tv'].includes(details.Kind) && details.ExternalId > 0) {
                        opened = await api(`Catalog/${details.Kind}/${details.ExternalId}/Open`, {}); id = opened.Item;
                    }
                    else opened = { Activity: await api('Items/' + id + '/Activity', { Action: 1 }) };
                    if (activityWarning(opened)) {
                        label.textContent = activityWarning(opened).trim();
                        const link = document.createElement('a'); link.textContent = 'Continue to Jellyfin'; link.href = (window.MediaHubNativeUrl || '') + '#!/details?' + new URLSearchParams({ id: normalize(id), serverId: ApiClient.serverId?.() || '' });
                        if (window.MediaHubOpenNative) link.addEventListener('click', event => { event.preventDefault(); void perform(link, () => window.MediaHubOpenNative(link.href)); }); panel.append(link); return;
                    }
                    const params = new URLSearchParams({ id: normalize(id), serverId: ApiClient.serverId?.() || '' });
                    if (window.MediaHubNativeUrl) { const destination = new URL(window.MediaHubNativeUrl); if (destination.protocol !== 'https:' || !window.MediaHubOpenNative) throw new Error('Invalid native destination.'); destination.hash = '#!/details?' + params; await window.MediaHubOpenNative(destination.href); }
                    else window.location.hash = '#!/details?' + params;
                }));
                if (!details.ForeignId) panel.append(actionButton('Save and subscribe', () => mutatePersonal('Me/Watchlist/' + details.NativeId + '/Bell', { Subscribed: true })));
            }
            if (details.ForeignId) {
                panel.append(actionButton('Save and subscribe', () => mutatePersonal(`Me/ExtraWatchlist/${details.Kind}/${encodeURIComponent(details.ForeignId)}`, { Subscribed: true })));
                const edition = document.createElement('select'); edition.setAttribute('is', 'emby-select'); edition.setAttribute('aria-label', 'Book edition');
                for (const id of details.Editions || []) { const option = document.createElement('option'); option.value = id; option.textContent = 'Edition ' + id; edition.append(option); }
                if (details.Kind === 'book') panel.append(edition);
                panel.append(actionButton('Prepare request', async () => {
                    const selected = edition.value || null;
                    const plan = await api('Extras/Requests/Prepare', { Kind: details.Kind, ForeignId: details.ForeignId, Edition: selected }); if (run !== catalogVersion) return;
                    const confirmation = document.createElement('div'); const summary = document.createElement('p'); summary.textContent = `Confirm request: ${plan.Title}${selected ? ' · Edition ' + selected : ''}`;
                    confirmation.append(summary, actionButton('Confirm request', async () => { const result = await api('Extras/Requests/' + plan.Id + '/Confirm', {}); if (run === catalogVersion) confirmation.textContent = result.State === 'completed' ? 'Requested.' + activityWarning(result) : 'Not confirmed. Check the media service before trying again.'; }),
                        actionButton('Cancel request plan', async () => { await api('Extras/Requests/' + plan.Id + '/Cancel', {}); if (run === catalogVersion) confirmation.remove(); })); panel.append(confirmation);
                })); return;
            }
            if (!['movie', 'tv'].includes(details.Kind) || details.ExternalId <= 0) return;
            if (!details.NativeId) panel.append(actionButton('Save and subscribe', () => mutatePersonal(`Me/Watchlist/${details.Kind}/${details.ExternalId}`, { Subscribed: true })));
            const choice = document.createElement('label'); const fourK = document.createElement('input'); fourK.type = 'checkbox'; fourK.setAttribute('is', 'emby-checkbox'); choice.append(fourK, document.createTextNode(' Request 4K'));
            panel.append(choice);
            const seasons = [];
            if (details.Kind === 'tv') {
                const legend = document.createElement('p'); legend.textContent = 'Select seasons, or leave unchecked to request all seasons.'; panel.append(legend);
                for (const number of details.Seasons || []) {
                    const label = document.createElement('label'), input = document.createElement('input'); input.type = 'checkbox'; input.setAttribute('is', 'emby-checkbox');
                    label.append(input, document.createTextNode(' Season ' + number + ' ')); panel.append(label); seasons.push({ input, number });
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
                    const receipt = await api('Requests/' + encodeURIComponent(plan.Id) + '/Confirm', {});
                    if (run !== catalogVersion) return;
                    confirmation.replaceChildren(); const result = document.createElement('p');
                    result.textContent = receipt.State === 'completed' ? 'Requested.' + activityWarning(receipt) : 'Not confirmed. Check your Seerr requests before trying again.'; confirmation.append(result);
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
            const list = view.querySelector('[data-catalog-list]'); list.replaceChildren(); view.querySelector('[data-media-details]').replaceChildren();
            view.querySelector('[data-catalog-notice]').textContent = result.Items.length ? 'Page ' + result.Page + ' of ' + result.Pages : 'No results.';
            for (const card of result.Items) {
                const row = document.createElement('li'); const title = document.createElement('span'); title.textContent = `${card.Title} · ${card.Status}${card.Is4k ? ' · 4K' : ''} `;
                row.append(title, actionButton('Details', () => mediaDetails(card))); list.append(row);
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
        function renderQueue(rows) {
            const list = view.querySelector('[data-deletion-list]'); list.replaceChildren();
            for (const row of rows) {
                const item = document.createElement('li');
                const state = { waiting: 'Waiting for consent', queued: 'Queued', ready: row.AutomaticDeletion ? 'Ready' : 'Ready · automatic removal off', blocked: 'Blocked', executing: 'Removing', checking: 'Confirming removal', cleanup: 'Unmonitoring', uncertain: 'Check required' }[row.State] || row.State;
                item.textContent = `${row.Title} · ${state} · ${row.Owner}${row.DueAt ? ' · ' + new Date(row.DueAt).toLocaleString() : ''} `;
                if (row.CanUndo) item.append(queueButton('Undo', 'Deletions/' + encodeURIComponent(row.Id) + '/Undo', {}));
                list.append(item);
            }
            view.querySelector('[data-deletions]').hidden = false;
        }
        function renderConsent(rows) {
            const list = view.querySelector('[data-consent-list]'); list.replaceChildren();
            for (const row of rows) {
                const item = document.createElement('li'); item.textContent = `${row.Title} · Allow deletion? `;
                const path = 'Me/Consent/' + encodeURIComponent(row.Id);
                item.append(queueButton('Agree', path, { Approve: true }), queueButton('Decline', path, { Approve: false })); list.append(item);
            }
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
                if (requestGeneration === generation && !(path === 'Settings' && error.status === 403) && path !== 'Messaging/Access') {
                    privateClear();
                    if ([401, 403].includes(error.status)) { hide(); status('Sign in again, then reopen Media Hub.'); }
                }
                const failure = new Error(error.responseJSON?.Error || error.responseJSON?.error || error.message || 'Action not confirmed. Check the result before retrying.'); failure.status = error.status; throw failure;
            }
        }
        function confirmGroup() {
            if (expectedGroup && normalize(lastGroup) === normalize(expectedGroup)) {
                status('Joined the watch party.'); expectedGroup = null;
                clearTimeout(confirmationTimer);
            }
        }
        function waitForGroup(groupId, message) {
            expectedGroup = groupId; status(message); clearTimeout(confirmationTimer); confirmGroup();
            if (expectedGroup) confirmationTimer = setTimeout(() => {
                if (expectedGroup) { expectedGroup = null; status('Join not confirmed. Check SyncPlay before retrying.'); }
            }, 15000);
        }
        function subscribe() {
            if (typeof ApiClient.subscribe !== 'function') throw new Error('Joining requires Jellyfin Web 12 with SyncPlay support.');
            const run = generation;
            unsubscribe = ApiClient.subscribe(['SyncPlayGroupUpdate'], ({ Data: update }) => {
                if (!update || run !== generation) return;
                if (update.Type === 'GroupJoined') { lastGroup = update.Data?.GroupId; confirmGroup(); }
                else if (['GroupLeft', 'NotInGroup', 'GroupDoesNotExist', 'JoinGroupDenied', 'LibraryAccessDenied'].includes(update.Type)) {
                    lastGroup = null; expectedGroup = null; clearTimeout(confirmationTimer); status('Party ended or access was denied. Check SyncPlay.');
                }
            });
        }
        async function refresh() {
            if (refreshBusy) return;
            refreshBusy = true;
            const run = generation;
            try {
                await Promise.all([
                    api('Status').then(data => {
                        activeParty = data.Party?.Active ? data.Party : null;
                        partyLabel.textContent = activeParty ? `${activeParty.GroupName} · ${activeParty.Participants.length} ${activeParty.Participants.length === 1 ? 'participant' : 'participants'}` : data.Party?.NeedsRefresh ? 'Party verification required. Check native SyncPlay; invitations are blocked.' : 'No active party.';
                        view.querySelector('[data-close]').hidden = !activeParty;
                        view.querySelector('[data-create] button').disabled = !data.Linked || Boolean(activeParty) || Boolean(data.Party?.NeedsRefresh);
                    }),
                    api('Me').then(data => applyPersonal(data)),
                     api('Deletions').then(renderQueue), api('Me/Consent').then(renderConsent), api('Me/Notifications').then(renderNotifications)
                ]);
            } finally { if (run === generation) refreshBusy = false; }
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
            const rules = personalState.DeletionRules || { DefaultDelayDays: 2, RecentActivityDays: 14 };
            const delay = preferencesForm.elements.deletionDelay;
            delay.min = rules.DefaultDelayDays; delay.max = rules.RecentActivityDays;
            delay.value = personalState.EffectiveDeletionDelayDays ?? p.DeletionDelayDays ?? rules.DefaultDelayDays;
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
            personalState = data; personalSection.hidden = false;
            if (!preferencesDirty || savedPreferences) loadPreferences();
            const external = data.ExternalWatchlist || [], extras = data.ExtraWatchlist || [];
            const signature = JSON.stringify([data.Watchlist, external, extras]);
            if (signature !== watchSignature) {
                watchSignature = signature; watchlist.replaceChildren();
                 for (const item of data.Watchlist) {
                     if ([...external, ...extras].some(w => w.NativeId && normalize(w.NativeId) === normalize(item.ItemId))) continue;
                    const row = document.createElement('li');
                    const title = document.createElement('span'); title.textContent = `${item.Title} · ${item.Kind} `; row.append(title);
                    const bell = document.createElement('button'); bell.setAttribute('is', 'emby-button'); bell.className = 'raised emby-button'; bell.type = 'button';
                    bell.textContent = item.Subscribed ? 'Notifications on' : 'Notifications off'; bell.setAttribute('aria-pressed', String(item.Subscribed));
                    bell.setAttribute('aria-label', `Notifications for ${item.Title}`);
                    bell.addEventListener('click', () => void mutatePersonal('Me/Watchlist/' + encodeURIComponent(item.ItemId) + '/Bell', { Subscribed: !item.Subscribed }));
                    const remove = document.createElement('button'); remove.setAttribute('is', 'emby-button'); remove.className = 'raised emby-button'; remove.type = 'button'; remove.textContent = 'Remove';
                    remove.setAttribute('aria-label', `Remove ${item.Title} from your watchlist`);
                    remove.addEventListener('click', () => void mutatePersonal('Me/Watchlist/' + encodeURIComponent(item.ItemId) + '/Remove', {}));
                     row.append(bell, document.createTextNode(' '), remove); watchlist.append(row);
                 }
                 for (const item of external) {
                     const row = document.createElement('li'); const label = document.createElement('span'); label.textContent = `${item.Title} · ${item.Kind} `;
                     const path = `Me/Watchlist/${item.Kind}/${item.ExternalId}`;
                     const bell = actionButton(item.Subscribed ? 'Notifications on' : 'Notifications off', () => mutatePersonal(path, { Subscribed: !item.Subscribed })); bell.setAttribute('aria-pressed', String(item.Subscribed));
                     row.append(label, bell, actionButton('Remove', () => mutatePersonal(path, { Subscribed: null }))); watchlist.append(row);
                 }
                 for (const item of extras) {
                     const row = document.createElement('li'); const text = document.createElement('span'); text.textContent = `${item.Title} · ${item.Kind} `;
                     const path = `Me/ExtraWatchlist/${item.Kind}/${encodeURIComponent(item.ForeignId)}`;
                     row.append(text, actionButton(item.Subscribed ? 'Notifications on' : 'Notifications off', () => mutatePersonal(path, { Subscribed: !item.Subscribed })), actionButton('Remove', () => mutatePersonal(path, { Subscribed: null }))); watchlist.append(row);
                 }
            }
            view.querySelector('[data-watchlist-empty]').hidden = data.Watchlist.length + external.length + extras.length > 0;
            updatePersonalControls();
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
                const item = document.createElement('li'); item.textContent = `${row.Title} · ${row.Status} `;
                if (!row.Seen) item.append(actionButton('Mark read', async () => { await api('Me/Notifications/' + encodeURIComponent(row.Id) + '/Seen', {}); await refresh(); })); list.append(item);
            }
        }
        view.querySelector('[data-watch-sync]').addEventListener('click', event => void perform(event.currentTarget, async () => {
            const result = await api('Me/Watchlist/Sync', {});
            view.querySelector('[data-sync-status]').textContent = result.Complete ? 'Synced.' : 'Sync pending. An earlier change is unconfirmed; no automatic resend.';
            await refresh();
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
        view.querySelector('[data-create]').addEventListener('submit', event => {
            event.preventDefault(); const form = event.currentTarget;
            void perform(form.querySelector('button'), async () => {
                const result = await api('Parties', { Name: form.elements.name.value.trim() });
                waitForGroup(result.GroupId, 'Party created. Confirming this player…'); await refresh();
            });
        });
        view.querySelector('[data-close]').addEventListener('click', event => void perform(event.currentTarget, async () => {
            if (!activeParty) return;
            await api('Parties/' + encodeURIComponent(activeParty.Id) + '/Close', {});
            status('Invitations closed. Current participants stay connected.'); await refresh();
        }));
        view.querySelector('[data-config]').addEventListener('submit', event => {
            event.preventDefault(); const form = event.currentTarget;
            void perform(form.querySelector('button'), async () => {
                const result = await api('Settings', { Revision: revision, DiscordEnabled: form.elements.enabled.checked,
                    AutomaticDeletion: form.elements.automaticDeletion.checked,
                    RecentActivityDays: Number(form.elements.recentDays.value), DeletionDelayDays: Number(form.elements.deletionDays.value),
                    PublicJellyfinUrl: form.elements.public.value.trim(), AllowedGuilds: form.elements.guilds.value.split(',').map(id => id.trim()).filter(Boolean),
                    SeerrUrl: form.elements.seerr.value.trim(), DiscordToken: form.elements.token.value || null, SeerrKey: form.elements.key.value || null });
                revision = result.Revision; configSnapshot = result; form.elements.token.value = ''; form.elements.key.value = ''; tutorial.saved('config', result); status('Settings saved.');
            });
        });
        function applyFeatures(config) {
            const form = view.querySelector('[data-features]'), f = config.Features || {};
            for (const name of ['lidarr', 'readarr']) { const s = f[name === 'lidarr' ? 'Lidarr' : 'Readarr'] || {}; for (const field of ['Url', 'Root', 'Quality', 'Metadata']) form.elements[name + field].value = s[field] ?? (['Quality', 'Metadata'].includes(field) ? 0 : ''); form.elements[name + 'Requests'].checked = Boolean(s.Requests); form.elements[name + 'Limit'].value = s.DailyLimit ?? 10; form.elements[name + 'Key'].value = ''; }
            form.elements.operators.value = (f.Operators || []).join(','); form.elements.inboxChannels.value = (f.InboxChannels || []).join(',');
            for (const field of ['seerrOperators', 'messageContent', 'membersIntent']) form.elements[field].checked = Boolean(f[field[0].toUpperCase() + field.slice(1)]);
            form.elements.retention.value = f.MessageRetentionDays ?? 7; form.elements.maxRecipients.value = f.MaxRecipients ?? 100;
            form.elements.applicationId.value = f.DiscordApplicationId || ''; form.elements.clientSecret.value = ''; form.elements.oauthPkce.checked = f.OAuthPkce !== false;
            form.elements.activityEnabled.checked = Boolean(f.ActivityEnabled);
            form.elements.arrDestinations.value = JSON.stringify(f.ArrDestinations || [], null, 2);
        }
        view.querySelector('[data-features]').addEventListener('submit', event => {
            event.preventDefault(); const form = event.currentTarget;
            void perform(form.querySelector('[type="submit"]'), async () => {
                if (!configSnapshot) throw new Error('Reload settings first.');
                const service = name => ({ Url: form.elements[name + 'Url'].value.trim(), Key: form.elements[name + 'Key'].value || null, Root: form.elements[name + 'Root'].value.trim(), Quality: Number(form.elements[name + 'Quality'].value), Metadata: Number(form.elements[name + 'Metadata'].value), Requests: form.elements[name + 'Requests'].checked, DailyLimit: Number(form.elements[name + 'Limit'].value) });
                const ids = input => input.value.split(',').map(v => v.trim()).filter(Boolean);
                const result = await api('Settings', { Revision: revision, DiscordEnabled: configSnapshot.DiscordEnabled, PublicJellyfinUrl: configSnapshot.PublicJellyfinUrl, AllowedGuilds: configSnapshot.AllowedGuilds, SeerrUrl: configSnapshot.SeerrUrl, DiscordToken: null, SeerrKey: null,
                    Features: { Lidarr: service('lidarr'), Readarr: service('readarr'), Operators: ids(form.elements.operators), SeerrOperators: form.elements.seerrOperators.checked, MessageContent: form.elements.messageContent.checked, MembersIntent: form.elements.membersIntent.checked, InboxChannels: ids(form.elements.inboxChannels), MessageRetentionDays: Number(form.elements.retention.value), MaxRecipients: Number(form.elements.maxRecipients.value), DiscordApplicationId: form.elements.applicationId.value.trim(), DiscordClientSecret: form.elements.clientSecret.value || null, OAuthPkce: form.elements.oauthPkce.checked, ActivityEnabled: form.elements.activityEnabled.checked, ArrDestinations: JSON.parse(form.elements.arrDestinations.value) } });
                revision = result.Revision; configSnapshot = result; applyFeatures(result); tutorial.saved('features', result); status('Integrations saved.');
            });
        });
        async function show() {
            if (visible) return;
            visible = true;
             const run = ++generation; refreshBusy = false; clearInterval(timer); unsubscribe?.(); privateClear(); clearSettings(); clearPersonal(); clearDeletions(); clearCatalog();
            // Clear controls left busy by a previous page lifetime, not by this one.
            view.querySelectorAll('button').forEach(button => { button.disabled = false; button.removeAttribute('aria-busy'); });
            try {
                account = currentAccount();
                subscribe(); await refresh();
                view.querySelector('[data-sync-activity-theme]').hidden = Boolean(window.MediaHubNativeUrl);
                void messenger?.show();
                try { await handoffLink(); }
                catch (error) { if (run !== generation) return; view.querySelector('[data-handoff-controls]').replaceChildren(); view.querySelector('[data-handoff-notice]').textContent = error.message; }
                const ticket = params?.hubTicket;
                if (ticket) {
                    // Account-bound, one-use ticket, not a Jellyfin/Discord credential.
                    // Never place the ticket in the DOM, logs, or browser storage.
                    delete params.hubTicket;
                    const url = new URL(window.location.href);
                    url.hash = url.hash.replace(/&hubTicket=[^&]*/g, ''); window.history.replaceState(null, '', url);
                    const result = await api('Parties/Join', { Ticket: ticket });
                    waitForGroup(result.GroupId, 'Joining… Waiting for Jellyfin confirmation.');
                }
                timer = setInterval(() => { if (!document.hidden) void refresh().catch(error => { if (run === generation) status(error.message); }); }, 10000);
                // Do not render/rebuild forms during status updates: preserve drafts.
            } catch (error) { if (run === generation) { privateClear(); status(error.message); } }
            // Local elevated settings must remain repairable when untrusted old integration state is blocked.
            if (run === generation) try {
                const config = await api('Settings'); revision = config.Revision; configSnapshot = config; applyFeatures(config);
                const form = view.querySelector('[data-config]'); form.elements.enabled.checked = config.DiscordEnabled;
                form.elements.automaticDeletion.checked = Boolean(config.AutomaticDeletion);
                form.elements.recentDays.value = config.RecentActivityDays ?? 14; form.elements.deletionDays.value = config.DeletionDelayDays ?? 2;
                form.elements.public.value = config.PublicJellyfinUrl; form.elements.guilds.value = config.AllowedGuilds.join(','); form.elements.seerr.value = config.SeerrUrl;
                view.querySelector('[data-settings]').hidden = false;
                tutorial.ready(config);
                if (config.ConfigurationReviewRequired) status('Older unsigned settings are blocked. Review all destinations and permissions, explicitly save, then reopen Media Hub. Integrations remain off until reviewed.');
            } catch { if (run === generation) clearSettings(); }
        }
        view.addEventListener('viewshow', show);
        view.addEventListener('viewhide', hide);
    };
})();
