/* global ApiClient */
(function () {
    'use strict';
    window.JellyfinMediaMessenger = function (view, api) {
        const section = view.querySelector('[data-messenger]'), compose = view.querySelector('[data-message-compose]'), filters = view.querySelector('[data-message-filters]');
        const notice = view.querySelector('[data-message-status]'), inbox = view.querySelector('[data-message-inbox]'), preview = view.querySelector('[data-message-preview]');
        let visible = false, generation = 0, timer, busy = false, polling = false, page = 1, requestId, draft = 0, cached;
        const urls = new Set();
        const fresh = () => { requestId = window.crypto.randomUUID().replaceAll('-', ''); };
        function button(label, action) {
            const b = document.createElement('button'); b.type = 'button'; b.className = 'raised emby-button'; b.setAttribute('is', 'emby-button'); b.textContent = label;
            b.addEventListener('click', () => void perform(b, action)); return b;
        }
        async function perform(b, action) {
            if (b.disabled || busy) return; const run = generation; b.disabled = true; busy = true;
            try { await action(); } catch (e) { if (run === generation) notice.textContent = e.message; }
            finally { if (run === generation) { b.disabled = false; busy = false; } }
        }
        function clearUrls() { urls.forEach(url => window.URL.revokeObjectURL(url)); urls.clear(); }
        function hide() {
            generation++; visible = false; clearInterval(timer); busy = false; polling = false; section.hidden = true; clearUrls();
            compose.reset(); filters.reset(); inbox.replaceChildren(); preview.replaceChildren(); view.querySelector('[data-message-deliveries]').replaceChildren(); view.querySelector('[data-message-paging]').replaceChildren(); notice.textContent = ''; compose.elements.channels.replaceChildren(); requestId = undefined; cached = undefined;
        }
        async function bytes(path, options = {}) {
            const run = generation;
            if (!visible) throw new Error('A current verified session is required.');
            if (typeof ApiClient.mediaHubTransfer === 'function') { const body = await ApiClient.mediaHubTransfer(path, options); await api('Messaging/Access'); if (run !== generation || !visible) throw new Error('Account or page changed.'); return body; }
            const token = ApiClient.accessToken?.(); if (!token) throw new Error('A current Jellyfin browser session is required.');
            const url = new window.URL(ApiClient.getUrl('MediaHub/' + path), window.location.href);
            if (url.origin !== window.location.origin) throw new Error('File transfers require the current Jellyfin server origin.');
            const result = await window.fetch(url, { ...options, headers: { Authorization: 'MediaBrowser Token="' + encodeURIComponent(token) + '"' }, credentials: 'same-origin', redirect: 'error', cache: 'no-store' });
            if (!result.ok) throw new Error('File transfer not confirmed. Check access and file size.');
            // Revalidate the same authenticated client through the shared API before exposing transferred bytes.
            const body = options.method === 'POST' ? await result.json() : await result.blob(); await api('Messaging/Access');
            if (run !== generation || !visible) throw new Error('Account or page changed.'); return body;
        }
        async function file(message, key, name, image) {
            const path = 'Messaging/Inbox/' + encodeURIComponent(message) + '/Assets/' + encodeURIComponent(key) + (image ? '?preview=true' : '');
            const blob = await bytes(path); const url = window.URL.createObjectURL(blob); urls.add(url);
            if (image && blob.type === 'image/png') {
                const img = document.createElement('img'); img.src = url; img.alt = name; img.loading = 'lazy'; inbox.append(img);
            } else {
                const a = document.createElement('a'); a.href = url; a.download = name; a.rel = 'noopener'; a.textContent = 'Download ' + name; inbox.append(a); a.click();
            }
        }
        function render(rows) {
            clearUrls(); inbox.replaceChildren();
            for (const row of rows) {
                const article = document.createElement('article'), h = document.createElement('h3'), text = document.createElement('p');
                h.textContent = `${row.AuthorName} · ${row.GuildName || 'DM'}${row.DeliveryState ? ' · ' + row.DeliveryState : ''}`;
                text.textContent = row.Deleted ? 'Message deleted' : row.Content; article.append(h, text);
                if (!row.Deleted) {
                    for (const embed of row.Rich?.embeds || []) {
                        const e = document.createElement('blockquote'), title = document.createElement('h4'), body = document.createElement('p'); title.textContent = embed.title; body.textContent = embed.description; e.append(title, body);
                        for (const field of embed.fields || []) { const f = document.createElement('p'); f.textContent = `${field.name}: ${field.value}`; e.append(f); }
                        if (embed.footer) { const footer = document.createElement('p'); footer.textContent = embed.footer; e.append(footer); }
                        if (embed.image) e.append(button('View image', () => file(row.Id, embed.image, 'image', true))); article.append(e);
                    }
                    for (const asset of row.Rich?.attachments || []) article.append(button('Download ' + asset.filename, () => file(row.Id, asset.key, asset.filename, false)));
                }
                inbox.append(article);
            }
        }
        function deliveries(jobs) {
            const list = view.querySelector('[data-message-deliveries]'); list.replaceChildren();
            for (const job of jobs) {
                const row = document.createElement('section'), title = document.createElement('h4'); title.textContent = `${job.Mode} · ${new Date(job.Created).toLocaleString()}`; row.append(title);
                for (const d of job.Deliveries) { const text = document.createElement('p'); text.textContent = `${d.Destination} · ${d.State === 'uncertain' ? 'Not confirmed · check Discord before a new send' : d.State}`; row.append(text); }
                list.append(row);
            }
        }
        async function refresh() {
            if (!visible || polling) return; polling = true; const run = generation;
            const params = new window.URLSearchParams({ kind: filters.elements.kind.value, query: filters.elements.query.value.trim(), page: String(page) });
            try {
                const [messages, jobs] = await Promise.all([api('Messaging/Inbox?' + params), api('Messaging/History')]);
                if (run !== generation) return; render(messages.Messages); deliveries(jobs);
                const paging = view.querySelector('[data-message-paging]'); paging.replaceChildren();
                if (page > 1) paging.append(button('Previous', async () => { page--; await refresh(); }));
                if (messages.HasNext) paging.append(button('Next', async () => { page++; await refresh(); }));
            } catch (e) {
                if (run === generation) {
                    if ([401, 403].includes(e.status)) hide();
                    else { clearUrls(); inbox.replaceChildren(); preview.replaceChildren(); view.querySelector('[data-message-deliveries]').replaceChildren(); }
                    notice.textContent = e.message + ([401, 403].includes(e.status) ? '' : ' Your draft was kept.');
                }
                throw e;
            }
            finally { if (run === generation) polling = false; }
        }
        filters.addEventListener('submit', event => { event.preventDefault(); page = 1; void perform(filters.querySelector('button'), refresh); });
        compose.addEventListener('input', () => { draft++; fresh(); cached = undefined; preview.replaceChildren(); });
        compose.addEventListener('change', () => { draft++; fresh(); cached = undefined; preview.replaceChildren(); });
        compose.addEventListener('submit', event => {
            event.preventDefault();
            void perform(compose.querySelector('[type="submit"]'), async () => {
                const run = generation, version = draft, id = requestId;
                const targets = [...compose.elements.channels.selectedOptions].map(option => JSON.parse(option.value));
                if (compose.elements.recipient.value.trim()) {
                    if (targets.length) throw new Error('Choose channels or one DM, not both.');
                    targets.push({ Kind: 'user', Id: compose.elements.recipient.value.trim(), Guild: compose.elements.guild.value.trim() });
                }
                const files = [...compose.elements.files.files];
                if (files.length > 4 || files.some(f => !f.size || f.size > 8 * 1024 * 1024) || files.reduce((n, f) => n + f.size, 0) > 16 * 1024 * 1024) throw new Error('Attachments exceed their size limit.');
                const selection = cached?.id === id ? cached.selection : { Mode: compose.elements.mode.value, Content: compose.elements.content.value, Targets: targets, Uploads: [], Members: compose.elements.members.checked, RequestId: id };
                if (cached?.id !== id) { for (const f of files) { const data = new window.FormData(); data.append('file', f); selection.Uploads.push((await bytes('Messaging/Uploads', { method: 'POST', body: data })).Id); } cached = { id, selection }; }
                if (version !== draft || run !== generation) throw new Error('Draft changed. Review it before sending.');
                if (selection.Mode === 'normal') {
                    const receipt = await api('Messaging/Chat', selection); if (run !== generation) return;
                    notice.textContent = 'Message queued. Check deliveries for confirmation.'; deliveries([receipt]); return;
                }
                const plan = await api('Messaging/Prepare', selection); if (run !== generation || version !== draft) return;
                preview.replaceChildren(); const content = document.createElement('p'); content.textContent = plan.Content; const targetsView = document.createElement('ul');
                for (const destination of plan.Destinations) { const li = document.createElement('li'); li.textContent = destination.Label; targetsView.append(li); }
                preview.append(content, targetsView, button('Confirm announcement', async () => {
                    if (version !== draft) throw new Error('Draft changed. Preview again.'); const result = await api('Messaging/Plans/' + plan.Id + '/Confirm', {});
                    if (run !== generation) return; preview.replaceChildren(); notice.textContent = 'Announcement queued. Check each delivery.'; deliveries([result]);
                }), button('Cancel preview', async () => { await api('Messaging/Plans/' + plan.Id + '/Cancel', {}); if (run === generation) { preview.replaceChildren(); fresh(); cached = undefined; } }));
            });
        });
        async function show() {
            if (visible) return; const run = ++generation;
            try {
                const access = await api('Messaging/Access'); if (run !== generation || !access.Operator) return;
                const destinations = await api('Messaging/Destinations'); if (run !== generation) return;
                for (const dest of destinations) { const option = document.createElement('option'); option.value = JSON.stringify(dest.Target); option.textContent = dest.Label; compose.elements.channels.append(option); }
                visible = true; section.hidden = false; fresh(); await refresh();
                timer = setInterval(() => { if (!document.hidden && !busy) void refresh().catch(() => {}); }, 15000);
            } catch { if (run === generation) hide(); }
        }
        return { show, hide };
    };
})();
