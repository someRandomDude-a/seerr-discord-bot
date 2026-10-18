(function () {
    'use strict';
    window.JellyfinDeletionMonitoring = function (view, api, verifyAccount) {
        const section = view.querySelector('[data-monitoring]');
        if (!section) return { show() {}, hide() {}, refresh() {} };
        const form = section.querySelector('[data-monitor-filters]'), summary = section.querySelector('[data-monitor-summary]');
        const list = section.querySelector('[data-monitor-list]'), paging = section.querySelector('[data-monitor-paging]');
        const detail = section.querySelector('[data-monitor-detail]'), message = section.querySelector('[data-monitor-notice]');
        let active = false, generation = 0, pending = false, page = 1, selected, detailVersion = 0;
        const text = (tag, content, classes) => { const node = document.createElement(tag); node.textContent = content; if (classes) node.className = classes; return node; };
        const date = value => value ? new Date(value).toLocaleString() : 'Not recorded';
        const account = value => `${value.Name} · ${value.Id}${value.Disabled ? ' · disabled' : ''}${!value.Exists ? ' · unavailable' : ''}`;
        const option = (label, value) => { const node = text('option', label); node.value = value; return node; };
        function hide() {
            active = false; generation += 1; detailVersion += 1; pending = false; selected = undefined; page = 1;
            section.hidden = true; list.replaceChildren(); summary.replaceChildren(); paging.replaceChildren(); detail.replaceChildren(); message.textContent = ''; form.reset();
            form.elements.owner.replaceChildren(option('All requesters', ''));
        }
        function button(label, action, key) {
            const node = text('button', label, 'raised emby-button'); node.type = 'button'; node.setAttribute('is', 'emby-button');
            if (key) node.dataset.monitorFocus = key;
            node.addEventListener('click', () => void action()); return node;
        }
        async function inspect(id, focus = true) {
            const run = generation, version = ++detailVersion; selected = id;
            try {
                verifyAccount(); const value = await api('Admin/Deletions/' + encodeURIComponent(id)); verifyAccount();
                if (!active || run !== generation || version !== detailVersion) return;
                const heading = text('h3', value.Proposal.Title, 'sectionTitle'); heading.tabIndex = -1;
                detail.replaceChildren(heading, text('p', 'Administrator evidence · read-only snapshot. This does not reverify the upstream target, vote, retry or delete media.', 'fieldDescription'));
                const facts = document.createElement('dl');
                for (const [label, content] of [
                    ['Proposal', value.Proposal.Id], ['Requester', account(value.Proposal.Requester)], ['Authority', value.Proposal.RequestId > 0 ? 'Seerr request ' + value.Proposal.RequestId : 'Explicit native-item permission'],
                    ['State / checkpoint', `${value.Proposal.State} / ${value.Proposal.Checkpoint || 'none'}`], ['Consent/deadline integrity', value.Proposal.Integrity],
                    ['Created', date(value.Proposal.Created)], ['Eligible since', date(value.Proposal.EligibleSince)], ['Due', date(value.Proposal.DueAt)],
                    ['Minimum delay', value.Proposal.DelayDays + ' days'], ['Revision', value.Proposal.Revision], ['Target identity', value.Target],
                    ['Retained Arr proof', value.ProofIntegrity], ['Checkpoint updated', date(value.CheckpointUpdated)]
                ]) facts.append(text('dt', label), text('dd', String(content)));
                detail.append(facts);
                detail.append(text('h4', 'Activity and consent', 'sectionTitle'));
                detail.append(text('p', value.ActivityVerified ? 'Current activity scan succeeded. A vote applies only to its recorded activity timestamp.' : 'Current activity scan unavailable or incomplete. Do not interpret the empty list as no viewers.', 'fieldDescription'));
                const activity = document.createElement('ul'); activity.className = 'paperList';
                for (const row of value.Activity) activity.append(text('li', `${account(row.Account)} · last activity ${date(row.LastActivity)}`, 'listItem'));
                const votes = document.createElement('ul'); votes.className = 'paperList';
                for (const row of value.Votes) votes.append(text('li', `${account(row.Account)} · ${row.Approved ? 'Yes' : 'No'} · activity covered ${date(row.Activity)}`, 'listItem'));
                detail.append(activity, text('h4', 'Recorded votes', 'sectionTitle'), votes);
                const notices = document.createElement('ul'); notices.className = 'paperList';
                for (const row of value.Notices) notices.append(text('li', `${account(row.Account)} · ${row.Id} · activity ${date(row.Activity)} · Discord ${row.Delivery || 'not attempted'} · updated ${date(row.Updated)}`, 'listItem'));
                detail.append(text('h4', 'Consent notices and delivery backlog', 'sectionTitle'), notices);
                if (value.Proof) {
                    const evidence = document.createElement('details'), title = text('summary', 'Retained service, edition, path and file proof');
                    evidence.append(title, text('pre', JSON.stringify(value.Proof, null, 2))); detail.append(evidence);
                }
                if (!value.HistoricalTransitionsAvailable) detail.append(text('p', 'Retained history contains creation and the latest execution checkpoint, not a complete transition timeline. Earlier events were not recorded and are not reconstructed.', 'fieldDescription'));
                const timeline = document.createElement('div');
                async function history(page) {
                    try {
                        const result = await api('Admin/Deletions/' + encodeURIComponent(id) + '/History?page=' + page); verifyAccount();
                        if (!active || run !== generation || version !== detailVersion) return;
                        timeline.replaceChildren(text('h4', 'Authenticated transition history', 'sectionTitle'), text('p', result.CompleteFromCreation ? 'Recorded from proposal creation.' : 'Recording began after this proposal was created. Earlier events are unavailable, not reconstructed.', 'fieldDescription'));
                        const entries = document.createElement('ul'); entries.className = 'paperList';
                        for (const event of result.Items) entries.append(text('li', `${event.Sequence} · ${date(event.At)} · ${event.Kind} · ${event.State} · ${event.Actor ? account(event.Actor) : 'System verification'}${event.DueAt ? ' · deadline ' + date(event.DueAt) : ''}${event.Reason ? ' · ' + event.Reason : ''}`, 'listItem'));
                        timeline.append(entries, text('p', `${result.Total} retained events · integrity ${result.Integrity}`, 'fieldDescription'));
                        if (page > 1) timeline.append(button('Newer events', () => history(page - 1)));
                        if (page * result.PageSize < result.Total) timeline.append(button('Older events', () => history(page + 1)));
                    } catch (error) { if (run === generation && version === detailVersion) { timeline.replaceChildren(text('p', 'History could not be verified. ' + error.message, 'fieldDescription')); if ([401, 403].includes(error.status)) hide(); } }
                }
                detail.append(button('Browse authenticated history', () => history(1)), timeline);
                if (focus) heading.focus();
            } catch (error) {
                if (run !== generation || version !== detailVersion) return;
                detail.replaceChildren(); if ([401, 403].includes(error.status)) hide(); else message.textContent = error.message;
            }
        }
        async function load() {
            if (!active || pending) return;
            pending = true; const run = generation;
            message.textContent = 'Loading administrator snapshot…';
            try {
                verifyAccount(); const query = new URLSearchParams({ state: form.elements.state.value, query: form.elements.query.value.trim(), sort: form.elements.sort.value, page: String(page) });
                if (form.elements.owner.value) query.set('owner', form.elements.owner.value);
                const data = await api('Admin/Deletions?' + query); verifyAccount();
                if (!active || run !== generation) return;
                const focused = list.contains(document.activeElement) ? document.activeElement.dataset.monitorFocus : null;
                summary.replaceChildren(text('p', `Snapshot ${date(data.GeneratedAt)} · ${data.Total} matching proposals · automatic deletion ${data.AutomaticDeletion ? 'enabled' : 'off'}`, 'fieldDescription'));
                const totals = document.createElement('ul'); totals.className = 'paperList';
                for (const [state, count] of Object.entries(data.Counts)) totals.append(text('li', `${state}: ${count}`, 'listItem'));
                summary.append(totals);
                const owner = form.elements.owner.value; form.elements.owner.replaceChildren(option('All requesters', ''));
                for (const row of data.Accounts) form.elements.owner.append(option(account(row), row.Id));
                form.elements.owner.value = owner;
                list.replaceChildren();
                for (const row of data.Items) {
                    const item = document.createElement('li'); item.className = 'listItem';
                    const body = document.createElement('div'); body.className = 'listItemBody';
                    body.append(text('h3', row.Title, 'listItemBodyText'), text('p', `${row.State} · ${account(row.Requester)} · request ${row.RequestId} · created ${date(row.Created)} · due ${date(row.DueAt)} · ${row.Integrity}`, 'listItemBodyText secondary'));
                    item.append(body, button('Inspect evidence', () => inspect(row.Id), row.Id)); list.append(item);
                }
                if (!data.Items.length) list.append(text('li', 'No proposals match these filters.', 'listItem'));
                paging.replaceChildren(text('p', `Page ${data.Page} of ${Math.max(1, Math.ceil(data.Total / data.PageSize))}`, 'fieldDescription'));
                if (data.Page > 1) paging.append(button('Previous', () => { page -= 1; return load(); }));
                if (data.Page * data.PageSize < data.Total) paging.append(button('Next', () => { page += 1; return load(); }));
                if (focused) [...list.querySelectorAll('[data-monitor-focus]')].find(node => node.dataset.monitorFocus === focused)?.focus();
                message.textContent = '';
                if (selected) await inspect(selected, false);
            } catch (error) {
                if (run !== generation) return;
                // Never leave private evidence displayed after a denied or unverifiable read.
                list.replaceChildren(); summary.replaceChildren(); detail.replaceChildren(); paging.replaceChildren();
                if ([401, 403].includes(error.status)) hide(); else message.textContent = error.message;
            } finally { if (run === generation) pending = false; }
        }
        form.addEventListener('submit', event => { event.preventDefault(); if (active) { generation += 1; pending = false; page = 1; selected = undefined; detailVersion += 1; detail.replaceChildren(); void load(); } });
        view.addEventListener('mediahubsettingshidden', hide);
        return {
            show() { if (window.MediaHubNativeUrl || active) return; verifyAccount(); active = true; section.hidden = false; void load(); },
            hide,
            refresh() { if (active && form.elements.live.checked) return load(); }
        };
    };
})();
