/* global ApiClient */
(function () {
    'use strict';
    if (window.mediaHubItemControls) return;
    const normalize = value => String(value || '').replaceAll('-', '').toLowerCase();
    let account = '', revision, watches = [], busy = false, generation = 0, scheduled = false;
    const nodes = new Set();
    function clear() {
        generation += 1; nodes.forEach(node => node.remove()); nodes.clear(); watches = []; revision = undefined;
    }
    function client() { return window.ApiClient; }
    function current() { const user = normalize(client()?.getCurrentUserId?.()); return user ? user + '@' + normalize(client()?.serverId?.()) : ''; }
    async function api(path, body) {
        const run = generation, user = account;
        if (!user || current() !== user) { clear(); account = current(); throw new Error('Account changed.'); }
        try {
            const result = await client().ajax({ type: body ? 'POST' : 'GET', url: client().getUrl('MediaHub/' + path),
                contentType: 'application/json', dataType: 'json', data: body ? JSON.stringify(body) : undefined });
            if (generation !== run || current() !== user) { if (run === generation) { clear(); account = current(); } throw new Error('Account changed.'); }
            return result;
        } catch (error) {
            if (run === generation && [401, 403, 404].includes(error.status)) clear();
            throw new Error(error.responseJSON?.Error || error.responseJSON?.error || error.message || 'Not confirmed. Check Media Hub before retrying.');
        }
    }
    function button(label) {
        const node = document.createElement('button'); node.type = 'button'; node.setAttribute('is', 'emby-button');
        node.className = 'raised emby-button'; node.textContent = label; return node;
    }
    async function refresh() {
        const data = await api('Me'); revision = data.Revision; watches = data.Watchlist; scan();
    }
    function scan() {
        if (current() !== account) { clear(); account = current(); return; }
        if (!account || revision === undefined) return;
        nodes.forEach(node => { if (!node.isConnected) nodes.delete(node); });
        for (const card of document.querySelectorAll('.card[data-id][data-type]')) {
            const id = normalize(card.dataset.id);
            if (card.tagName === 'BUTTON' || !/^[0-9a-f]{32}$/.test(id) || !['Movie', 'Series', 'Episode', 'MusicAlbum', 'Audio', 'Book', 'AudioBook'].includes(card.dataset.type)) continue;
            if (card.dataset.serverid && normalize(card.dataset.serverid) !== normalize(client().serverId?.())) continue;
            let controls = [...card.children].find(node => node.hasAttribute('data-mediahub-controls'));
            if (controls && controls.dataset.item !== id) { controls.remove(); nodes.delete(controls); controls = null; }
            if (controls) {
                const bell = controls.querySelector('[data-bell]'); const followed = watches.find(item => normalize(item.ItemId) === id)?.Subscribed === true;
                bell.textContent = followed ? 'Unsubscribe' : 'Subscribe'; bell.setAttribute('aria-pressed', String(followed)); continue;
            }
            controls = document.createElement('div'); controls.dataset.mediahubControls = ''; controls.dataset.item = id; nodes.add(controls);
            const bell = button('Subscribe'); bell.dataset.bell = ''; const status = document.createElement('span'); status.setAttribute('role', 'status'); status.setAttribute('aria-live', 'polite');
            controls.addEventListener('click', event => { event.stopPropagation(); });
            controls.addEventListener('keydown', event => { event.stopPropagation(); });
            bell.addEventListener('click', async event => {
                event.preventDefault(); if (busy) return; busy = true; bell.disabled = true;
                const run = generation;
                try {
                    const on = watches.find(item => normalize(item.ItemId) === id)?.Subscribed === true;
                    const result = await api('Me/Watchlist/' + id + '/Bell', { Revision: revision, Subscribed: !on });
                    revision = result.Revision; watches = result.Watchlist; status.textContent = 'Saved.'; scan();
                } catch (error) { if (run === generation) { status.textContent = error.message; try { await refresh(); } catch { /* No automatic mutation replay. */ } } }
                finally { busy = false; bell.disabled = false; }
            });
            controls.append(bell);
            if (['Movie', 'Series'].includes(card.dataset.type)) {
                const deletion = button('Propose deletion');
                deletion.addEventListener('click', async event => {
                    event.preventDefault(); if (busy) return; busy = true; deletion.disabled = true; const run = generation;
                    try {
                        controls.querySelectorAll('[data-request-choice]').forEach(node => node.remove());
                        const choices = await api('Items/' + id + '/DeletionRequests');
                        if (!choices.length) status.textContent = 'No owned request found.';
                        for (const choice of choices) {
                            const confirm = button('Confirm deletion proposal · ' + (choice.Is4k ? '4K' : 'Standard')); confirm.dataset.requestChoice = '';
                            confirm.addEventListener('click', async () => {
                                if (busy) return; busy = true; confirm.disabled = true;
                                try { await api('Deletions', { RequestId: choice.RequestId, Item: id }); status.textContent = 'Deletion proposed.'; }
                                catch (error) { if (run === generation) status.textContent = error.message; }
                                finally { busy = false; confirm.disabled = false; }
                            }); controls.append(confirm);
                        }
                    } catch (error) { if (run === generation) status.textContent = error.message; }
                    finally { busy = false; deletion.disabled = false; }
                }); controls.append(deletion);
            }
            controls.append(status); card.append(controls);
        }
    }
    function schedule() {
        if (scheduled) return; scheduled = true;
        queueMicrotask(() => { scheduled = false; scan(); });
    }
    const observer = new MutationObserver(records => { if (records.some(record => !record.target.closest?.('[data-mediahub-controls]'))) schedule(); });
    observer.observe(document.documentElement, { childList: true, subtree: true, attributes: true, attributeFilter: ['data-id'] });
    let refreshing = false;
    async function tick() {
        const user = current();
        if (user !== account) { clear(); account = user; }
        if (!user || document.hidden || refreshing) return;
        try { if (window.sessionStorage?.getItem('mediahub.resume') === '1') {
            const code = window.sessionStorage.getItem('mediahub.handoff'); window.sessionStorage.removeItem('mediahub.resume');
            if (/^[0-9a-f]{48}$/i.test(code || '') && !window.location.hash.includes('name=mediahub') && !window.location.hash.includes('hubLink=')) {
                const page = new URL(client().getUrl('web/ConfigurationPage?name=mediahub'), window.location.href);
                if (page.origin !== window.location.origin) throw new Error('Open Media Hub on the signed-in server.');
                window.location.hash = '#!/userpluginsettings.html?pageUrl=' + encodeURIComponent(page.pathname + page.search) + '&hubLink=' + encodeURIComponent(code); return;
            }
        } } catch { /* Storage-disabled clients can use explicit code approval. */ }
        refreshing = true;
        try { await refresh(); } catch { /* Unsupported clients retain native behavior. */ }
        finally { refreshing = false; }
    }
    const timer = setInterval(tick, 10000); void tick();
    window.mediaHubItemControls = { stop: () => { observer.disconnect(); clearInterval(timer); clear(); } };
    window.addEventListener('pagehide', () => window.mediaHubItemControls.stop(), { once: true });
})();
