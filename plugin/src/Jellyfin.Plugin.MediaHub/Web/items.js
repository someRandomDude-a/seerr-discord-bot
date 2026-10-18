/* global ApiClient */
(function () {
    'use strict';
    if (window.mediaHubItemControls) return;
    const normalize = value => String(value || '').replaceAll('-', '').toLowerCase();
    let account = '', revision, watches = [], proposals = [], consent = [], notifications = [], busy = false, generation = 0, scheduled = false, detail, menu, menuRefresh, menuDismiss, menuOpening = false;
    const nodes = new Set();
    function clear() {
        generation += 1; menu?.replaceChildren(); try { menuDismiss?.(false); } catch { menu?.remove(); } menu = undefined; menuRefresh = undefined; menuDismiss = undefined; menuOpening = false; nodes.forEach(node => node.remove()); nodes.clear(); watches = []; proposals = []; consent = []; notifications = []; revision = undefined; detail = undefined;
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
        const node = document.createElement('button', 'emby-button'); node.type = 'button'; node.setAttribute('is', 'emby-button');
        node.className = 'raised emby-button'; node.textContent = label; return node;
    }
    function deletionHelp(proposal) {
        const help = document.createElement('button'); help.type = 'button'; help.className = 'paper-icon-button-light'; help.setAttribute('is', 'paper-icon-button-light');
        help.setAttribute('aria-label', 'Help about deletion'); help.title = 'Help about deletion'; help.setAttribute('aria-expanded', 'false');
        const icon = document.createElement('span'); icon.className = 'material-icons'; icon.setAttribute('aria-hidden', 'true'); icon.textContent = 'help_outline'; help.append(icon);
        const details = document.createElement('div'); details.className = 'fieldDescription'; details.hidden = true;
        for (const text of [
            proposal ? proposalText(proposal) + '.' : 'Your answer applies only to this deletion proposal.',
            'Every current recent viewer must agree or age out of the configured activity window (14 days by default). No blocks deletion while your activity remains recent. New activity invalidates consent.',
            'Deletion also requires the full configured delay (at least 2 days by default). A Yes is consent, not an immediate deletion. Existing deadlines never silently shorten.',
            'Only the original requester can propose deletion or Undo. There is no administrator ownership bypass. Files are removed only through verified, approved Radarr/Sonarr destinations after all checks pass.',
            'Automatic removal is off by default. When it is off, nothing is automatically deleted, even after consent and the delay. Unverified or uncertain state blocks deletion.',
            'Opening notifications or Help does not vote, mark updates read, or record media activity.'
        ]) { const paragraph = document.createElement('p'); paragraph.textContent = text; details.append(paragraph); }
        help.addEventListener('click', () => { details.hidden = !details.hidden; help.setAttribute('aria-expanded', String(!details.hidden)); });
        return { help, details };
    }
    function hubUrl(section) {
        const page = new window.URL(client().getUrl('web/ConfigurationPage?name=mediahub'), window.location.href);
        if (page.origin !== window.location.origin) throw new Error('Open Media Hub on the signed-in server.');
        // Plugin Pages resolves a leading slash through ApiClient.getUrl itself.
        // Pass a server-relative resource, not the already base-prefixed pathname.
        return '#/userpluginsettings.html?pageUrl=' + encodeURIComponent('/web/ConfigurationPage?name=mediahub') + '&hubSection=' + section;
    }
    function topNavigation() {
        // Jellyfin 12 keeps a hidden legacy header beside its active native MUI toolbar.
        // Reuse the active toolbar's own button classes, including its theme-generated rules.
        const toolbar = document.querySelector('header .MuiToolbar-root');
        const nativeButtons = [...(toolbar?.querySelectorAll('.MuiIconButton-root') || [])].filter(node => !node.closest('[data-mediahub-navigation]'));
        const nativeButton = nativeButtons.find(node => !node.querySelector('.MuiAvatar-root'));
        const header = toolbar ? (nativeButton || nativeButtons[0])?.parentElement || toolbar : document.querySelector('.skinHeader:not(.noHeaderRight) .headerRight');
        if (!header) return;
        for (const node of nodes) if (node.hasAttribute('data-mediahub-navigation') && node.parentElement !== header) { node.remove(); nodes.delete(node); }
        const nativeClass = nativeButton ? [...nativeButton.classList].filter(name => !['Mui-disabled', 'Mui-focusVisible'].includes(name)).join(' ') : toolbar ? 'headerButton paper-icon-button-light' : 'headerButton headerButtonRight paper-icon-button-light';
        const existing = header.querySelector('[data-mediahub-navigation]');
        if (existing) { for (const child of existing.children) if (child.className !== nativeClass) child.className = nativeClass; return; }
        const nav = document.createElement('div'); nav.dataset.mediahubNavigation = ''; nav.className = toolbar ? 'MuiBox-root' : 'headerRight'; nodes.add(nav);
        const watch = document.createElement('a'); watch.href = hubUrl('watchlist'); watch.className = nativeClass; watch.setAttribute('aria-label', 'Your watchlist'); watch.title = 'Your watchlist';
        const saved = document.createElement('span'); saved.className = 'material-icons'; saved.setAttribute('aria-hidden', 'true'); saved.textContent = 'bookmark'; watch.append(saved);
        const bell = toolbar ? document.createElement('button') : button(''); bell.type = 'button'; bell.className = nativeClass; bell.dataset.notificationBell = ''; bell.setAttribute('aria-haspopup', 'dialog');
        bell.addEventListener('click', () => { void showNotifications().catch(() => { if (current() === account) window.location.hash = hubUrl('notifications'); }); }); nav.append(watch, bell); header.append(nav);
    }
    function updateBell() {
        const bell = document.querySelector('[data-notification-bell]'); if (!bell) return;
        const unread = consent.length + notifications.filter(row => !row.Seen).length;
        const label = 'Notifications' + (unread ? ' · ' + unread + ' unread' : ' · no unread updates'); bell.setAttribute('aria-label', label); bell.title = label;
        const icon = document.createElement('span'); icon.className = 'material-icons'; icon.setAttribute('aria-hidden', 'true'); icon.textContent = unread ? 'notifications_active' : 'notifications_none';
        bell.replaceChildren(icon);
        if (unread) { const count = document.createElement('span'); count.className = 'countIndicator'; count.textContent = unread > 99 ? '99+' : String(unread); count.setAttribute('aria-hidden', 'true'); bell.append(count); }
    }
    function proposalText(row) {
        const stage = { waiting: 'Awaiting viewer consent', queued: 'Removal scheduled', ready: 'Removal delay elapsed', blocked: 'Removal blocked · review needed', uncertain: 'Removal outcome unconfirmed', executing: 'Removal in progress', checking: 'Removal being checked', cleanup: 'Removal cleanup pending' };
        return (stage[row.State] || 'Removal proposed') + (row.DueAt ? ' · ' + new Date(row.DueAt).toLocaleString() : '') + (!row.AutomaticDeletion ? ' · automatic removal off' : '');
    }
    function updateMarker(marker, pending) {
        marker.hidden = !pending.length; const label = pending.map(proposalText).join(' · ');
        if (marker.dataset.description === label) return;
        marker.dataset.description = label; marker.setAttribute('aria-label', 'Review media removal proposal · ' + label); marker.title = label;
        const icon = document.createElement('span'); icon.className = 'material-icons'; icon.setAttribute('aria-hidden', 'true'); icon.textContent = 'delete_outline';
        const text = document.createElement('span'); text.textContent = label; marker.replaceChildren(icon, text);
    }
    async function showNotifications() {
        if (!account || current() !== account) { clear(); account = current(); void tick(); return; }
        if (menu?.isConnected || menuOpening) return;
        const run = generation, returnFocus = document.querySelector('[data-notification-bell]');
        menuOpening = true; returnFocus?.setAttribute('aria-busy', 'true');
        try { await refresh(); } finally { if (run === generation) menuOpening = false; returnFocus?.removeAttribute('aria-busy'); }
        if (run !== generation || current() !== account) return;
        const dialog = document.createElement('dialog');
        menu = dialog; dialog.className = 'dialog formDialog'; dialog.setAttribute('role', 'dialog'); dialog.setAttribute('aria-modal', 'true'); dialog.setAttribute('aria-labelledby', 'mediaHubNotificationsTitle'); dialog.dataset.mediahubMenu = '';
        // Jellyfin's dialog theme targets divs and inherits body text. Reset only the
        // browser dialog's UA CanvasText default; no palette/font/theme is supplied here.
        dialog.style.color = 'inherit';
        let restoreFocus = true, closed = false;
        const cleanup = () => { if (closed) return; closed = true; dialog.remove(); if (menu === dialog) { menu = undefined; menuRefresh = undefined; menuDismiss = undefined; } if (restoreFocus && run === generation && current() === account) returnFocus?.focus(); };
        const dismiss = restore => { restoreFocus = restore; if (typeof dialog.close === 'function') dialog.close(); cleanup(); };
        menuDismiss = dismiss;
        const panel = document.createElement('div'); panel.className = 'formDialogContent smoothScrollY padded-left padded-right';
        const heading = document.createElement('h2'); heading.className = 'sectionTitle'; heading.id = 'mediaHubNotificationsTitle'; heading.textContent = 'Notifications';
        const close = button('Close'); close.addEventListener('click', () => dismiss(true));
        const list = document.createElement('div'); list.className = 'paperList'; const status = document.createElement('p'); status.setAttribute('role', 'status'); status.setAttribute('aria-live', 'polite');
        const expandedHelp = new Set();
        const render = () => {
            for (const id of expandedHelp) if (!consent.some(notice => notice.Id === id)) expandedHelp.delete(id);
            list.replaceChildren();
            if (!consent.length && !notifications.length) { const empty = document.createElement('p'); empty.textContent = 'You’re all caught up.'; list.append(empty); }
            for (const notice of consent) {
                const box = document.createElement('div'); box.className = 'listItem'; const body = document.createElement('div'); body.className = 'listItemBody';
                const title = document.createElement('h3'); title.className = 'listItemBodyText'; title.textContent = notice.Title; title.id = 'mediaHubBellConsent-' + encodeURIComponent(notice.Id);
                const text = document.createElement('p'); text.className = 'listItemBodyText secondary'; text.textContent = 'Allow deletion?';
                const actions = document.createElement('div'); actions.setAttribute('role', 'group'); actions.setAttribute('aria-label', 'Allow deletion?'); actions.setAttribute('aria-describedby', title.id); body.append(title, text, actions); box.append(body);
                for (const approve of [true, false]) {
                    const choice = button(approve ? 'Yes' : 'No'); if (approve) choice.classList.add('button-submit');
                    choice.dataset.focusKey = 'consent:' + notice.Id + ':' + approve;
                    choice.addEventListener('click', async () => {
                        if (busy) return; busy = true; box.querySelectorAll('button').forEach(node => { node.disabled = true; });
                        try { await api('Me/Consent/' + encodeURIComponent(notice.Id), { Approve: approve }); await refresh(); if (run === generation) { render(); status.textContent = approve ? 'Yes saved.' : 'No saved.'; close.focus(); } }
                        catch (error) { if (run === generation) status.textContent = error.message; }
                        finally { busy = false; box.querySelectorAll('button').forEach(node => { node.disabled = false; }); }
                    }); actions.append(choice);
                }
                const explanation = deletionHelp(proposals.find(row => row.Id === notice.Proposal));
                explanation.details.id = title.id + '-help'; explanation.details.hidden = !expandedHelp.has(notice.Id);
                explanation.help.setAttribute('aria-controls', explanation.details.id); explanation.help.setAttribute('aria-expanded', String(!explanation.details.hidden)); explanation.help.dataset.focusKey = 'help:' + notice.Id;
                explanation.help.addEventListener('click', () => { if (explanation.details.hidden) expandedHelp.delete(notice.Id); else expandedHelp.add(notice.Id); });
                actions.append(explanation.help); body.append(explanation.details);
                list.append(box);
            }
            for (const notice of notifications) {
                const box = document.createElement('div'); box.className = 'listItem'; const text = document.createElement('div'); text.className = 'listItemBody'; text.textContent = notice.Title + ' · ' + notice.Status; box.append(text);
                if (!notice.Seen) { const seen = button('Mark read'); seen.dataset.focusKey = 'read:' + notice.Id; seen.addEventListener('click', async () => {
                    if (busy) return; busy = true; seen.disabled = true;
                    try { await api('Me/Notifications/' + encodeURIComponent(notice.Id) + '/Seen', {}); await refresh(); if (run === generation) render(); }
                    catch (error) { if (run === generation) status.textContent = error.message; } finally { busy = false; seen.disabled = false; }
                }); box.append(seen); } list.append(box);
            }
        };
        render(); menuRefresh = () => { const focused = document.activeElement; const key = list.contains(focused) ? focused.dataset.focusKey : null; render(); if (key) ([...list.querySelectorAll('[data-focus-key]')].find(node => node.dataset.focusKey === key) || close).focus(); };
        const all = document.createElement('a'); all.className = 'button-link'; all.href = hubUrl('notifications'); all.textContent = 'All notifications';
        panel.append(heading, close, list, status, all); dialog.append(panel);
        dialog.addEventListener('close', cleanup, { once: true });
        dialog.addEventListener('keydown', event => {
            if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); dismiss(true); }
            if (event.key === 'Tab') {
                const controls = [...dialog.querySelectorAll('button:not(:disabled),a[href],[tabindex]')].filter(node => node.tabIndex >= 0 && !node.closest('[hidden]'));
                const first = controls[0], last = controls[controls.length - 1];
                if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last?.focus(); }
                else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first?.focus(); }
            }
        });
        document.body.append(dialog); if (typeof dialog.showModal === 'function') dialog.showModal(); else dialog.setAttribute('open', ''); close.focus();
    }
    async function refresh() {
        const run = generation, user = account;
        const before = JSON.stringify([proposals, consent, notifications]);
        const [data, queue, notices, feed] = await Promise.all([api('Me'), api('Deletions'), api('Me/Consent'), api('Me/Notifications')]);
        if (run !== generation || account !== user || current() !== user) return;
        if (revision === undefined || data.Revision >= revision) { revision = data.Revision; watches = data.Watchlist; }
        proposals = Array.isArray(queue) ? queue : []; consent = Array.isArray(notices) ? notices : []; notifications = Array.isArray(feed) ? feed : [];
        const target = detailTarget(); detail = undefined;
        if (target) { const item = await api('Items/' + target.id + '/Controls'); if (normalize(item.Id) === target.id) detail = { ...target, kind: item.Kind }; }
        if (run !== generation || account !== user || current() !== user) return;
        scan();
        if (before !== JSON.stringify([proposals, consent, notifications])) menuRefresh?.();
    }
    function detailTarget() {
        if (!/^#!?\/details\?/.test(window.location.hash)) return null;
        const params = new window.URLSearchParams(window.location.hash.split('?')[1]), id = normalize(params.get('id'));
        if (!/^[0-9a-f]{32}$/.test(id) || params.get('serverId') && normalize(params.get('serverId')) !== normalize(client()?.serverId?.())) return null;
        const pages = [...document.querySelectorAll('.itemDetailPage')].filter(page => !page.hidden && !page.classList.contains('hide') && page.getAttribute('aria-hidden') !== 'true');
        if (pages.length !== 1) return null;
        const host = pages[0].querySelector('.detailButtons') || pages[0].querySelector('.detailPagePrimaryContainer');
        return host ? { host, id } : null;
    }
    function scan() {
        if (current() !== account) { clear(); account = current(); return; }
        if (!account || revision === undefined) return;
        topNavigation(); updateBell();
        nodes.forEach(node => { if (!node.isConnected) nodes.delete(node); });
        const candidates = [...document.querySelectorAll('.card[data-id][data-type]')].filter(card => !card.dataset.serverid || normalize(card.dataset.serverid) === normalize(client().serverId?.())).map(card => ({ host: card, id: normalize(card.dataset.id), kind: card.dataset.type }));
        const currentDetail = detailTarget(); if (detail && currentDetail?.host === detail.host && currentDetail.id === detail.id) candidates.push(detail);
        for (const { host: card, id, kind } of candidates) {
            if (card.tagName === 'BUTTON' || !/^[0-9a-f]{32}$/.test(id) || !['Movie', 'Series', 'Episode', 'MusicAlbum', 'Audio', 'Book', 'AudioBook'].includes(kind)) continue;
            if (card.dataset.serverid && normalize(card.dataset.serverid) !== normalize(client().serverId?.())) continue;
            let controls = [...card.children].find(node => node.hasAttribute('data-mediahub-controls'));
            if (controls && controls.dataset.item !== id) { controls.remove(); nodes.delete(controls); controls = null; }
            if (controls) {
                const saved = watches.some(item => normalize(item.ItemId) === id); const add = controls.querySelector('[data-watch-add]'); add.textContent = saved ? 'In watchlist' : 'Add to watchlist'; add.disabled = saved; add.setAttribute('aria-pressed', String(saved));
                const bell = controls.querySelector('[data-bell]'); const followed = watches.find(item => normalize(item.ItemId) === id)?.Subscribed === true;
                bell.textContent = followed ? 'Unsubscribe' : 'Subscribe'; bell.setAttribute('aria-pressed', String(followed));
                const badge = controls.querySelector('[data-removal-marker]'), pending = proposals.filter(row => normalize(row.Item) === id); updateMarker(badge, pending);
                continue;
            }
            controls = document.createElement('div'); controls.dataset.mediahubControls = ''; controls.dataset.item = id; nodes.add(controls);
            const bell = button('Subscribe'); bell.dataset.bell = ''; const status = document.createElement('span'); status.setAttribute('role', 'status'); status.setAttribute('aria-live', 'polite');
            controls.addEventListener('click', event => { event.stopPropagation(); });
            controls.addEventListener('keydown', event => { event.stopPropagation(); });
            const marker = document.createElement('a'); marker.dataset.removalMarker = ''; marker.className = 'mediaInfoItem mediaInfoText button-link'; marker.href = hubUrl('notifications'); marker.setAttribute('aria-label', 'Review media removal proposal');
            const add = button('Add to watchlist'); add.dataset.watchAdd = ''; add.addEventListener('click', async event => {
                event.preventDefault(); if (busy) return; busy = true; add.disabled = true; const run = generation;
                try { const result = await api('Me/Watchlist/' + id + '/Add', { Revision: revision }); revision = result.Revision; watches = result.Watchlist; status.textContent = 'Saved to your watchlist. Notification choice unchanged.'; }
                catch (error) { if (run === generation) status.textContent = error.message; }
                finally { busy = false; if (run === generation) scan(); }
            }); controls.append(marker, add);
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
            if (['Movie', 'Series'].includes(kind)) {
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
                                try { await api('Deletions', { RequestId: choice.RequestId, Item: id }); status.textContent = 'Deletion proposed.'; await refresh(); }
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
        // Apply the same confirmed state to newly mounted card/detail controls.
        for (const controls of document.querySelectorAll('[data-mediahub-controls]')) {
            const saved = watches.some(item => normalize(item.ItemId) === controls.dataset.item), add = controls.querySelector('[data-watch-add]'); add.disabled = saved; add.textContent = saved ? 'In watchlist' : 'Add to watchlist'; add.setAttribute('aria-pressed', String(saved));
            const pending = proposals.filter(row => normalize(row.Item) === controls.dataset.item), marker = controls.querySelector('[data-removal-marker]'); updateMarker(marker, pending);
        }
    }
    function schedule() {
        if (scheduled) return; scheduled = true;
        queueMicrotask(() => { scheduled = false; scan(); });
    }
    const observer = new MutationObserver(records => { if (records.some(record => !record.target.closest?.('[data-mediahub-controls],[data-mediahub-navigation],[data-mediahub-menu]'))) schedule(); });
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
                window.location.hash = '#/userpluginsettings.html?pageUrl=' + encodeURIComponent('/web/ConfigurationPage?name=mediahub') + '&hubLink=' + encodeURIComponent(code); return;
            }
        } } catch { /* Storage-disabled clients can use explicit code approval. */ }
        refreshing = true;
        try { await refresh(); } catch { /* Unsupported clients retain native behavior. */ }
        finally { refreshing = false; }
    }
    const timer = setInterval(tick, 10000); void tick();
    const navigate = () => { clear(); schedule(); void tick(); };
    window.addEventListener('hashchange', navigate);
    // Jellyfin bubbles legacy pagehide events from DIVs when changing views, including
    // the login page. They must not permanently stop this global bootstrap.
    const unload = event => {
        if (event.target !== window && !(window.PageTransitionEvent && event instanceof window.PageTransitionEvent)) return;
        if (event.persisted) { clear(); account = ''; } else window.mediaHubItemControls.stop();
    };
    const restore = () => { void tick(); };
    window.mediaHubItemControls = { stop: () => { observer.disconnect(); clearInterval(timer); window.removeEventListener('hashchange', navigate); window.removeEventListener('pagehide', unload); window.removeEventListener('pageshow', restore); clear(); } };
    window.addEventListener('pagehide', unload); window.addEventListener('pageshow', restore);
})();
