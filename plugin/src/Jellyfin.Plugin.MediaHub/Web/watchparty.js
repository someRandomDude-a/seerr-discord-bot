(function () {
    'use strict';
    window.JellyfinMediaWatchParty = function (view, api, verifyAccount = () => {}) {
        const section = view.querySelector('[data-watchparty]');
        const message = section.querySelector('[data-watchparty-status]');
        const form = section.querySelector('[data-watchparty-create]');
        const actions = section.querySelector('[data-watchparty-actions]');
        const links = section.querySelector('[data-watchparty-links]');
        const invite = section.querySelector('[data-watchparty-invite]');
        const end = section.querySelector('[data-watchparty-end]');
        const manage = section.querySelector('[data-watchparty-manage]'), management = section.querySelector('[data-watchparty-management]');
        const refreshButton = section.querySelector('[data-watchparty-refresh]');
        let active = false, busy = false, reading, needsReview = false, version = 0, state, saved = [];
        const validId = value => typeof value === 'string' && /^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$/i.test(value) && !/^0{8}-0{4}-0{4}-0{4}-0{12}$/.test(value);
        function clearLinks() { saved = []; links.replaceChildren(); management.replaceChildren(); }
        function controls() {
            form.hidden = !active || !state?.Available || state.Active;
            actions.hidden = !active || !state?.Active;
            invite.hidden = end.hidden = manage.hidden = !state?.Owner;
            section.querySelectorAll('button').forEach(button => { button.disabled = busy || needsReview; });
            refreshButton.disabled = busy;
            invite.disabled ||= saved.length >= 4;
        }
        function render() {
            links.replaceChildren();
            for (const link of saved) {
                const row = document.createElement('li'); row.className = 'listItem';
                const body = document.createElement('div'); body.className = 'listItemBody listItemBodyText';
                const a = document.createElement('a'); a.className = 'button-link'; a.href = link.Url; a.rel = 'noreferrer noopener'; a.referrerPolicy = 'no-referrer'; a.textContent = 'Signed-in invitation · share deliberately';
                // Link navigation/copy menus do not pass through api(). Apply the same local
                // identity boundary before using a previously generated bearer link.
                for (const type of ['click', 'auxclick', 'contextmenu']) a.addEventListener(type, event => {
                    try { verifyAccount(); } catch { event.preventDefault(); }
                });
                const expiry = document.createElement('p'); expiry.className = 'fieldDescription'; expiry.textContent = 'Expires by ' + new Date(link.ExpiresAt).toLocaleString() + '. Link is not bound to a recipient.';
                const revoke = document.createElement('button', 'emby-button'); revoke.setAttribute('is', 'emby-button'); revoke.className = 'raised emby-button'; revoke.type = 'button'; revoke.textContent = 'Revoke invitation';
                revoke.addEventListener('click', () => void mutate(async current => {
                    await api('WatchParty/' + link.PartyId + '/Invites/' + link.InviteId + '/Revoke', {});
                    if (!current()) return;
                    saved = saved.filter(item => item.InviteId !== link.InviteId); render();
                    message.textContent = 'Invitation revoked.';
                }));
                body.append(a, expiry); row.append(body, revoke); links.append(row);
            }
            controls();
        }
        function apply(next) {
            if (!next || next.Available !== true || typeof next.Active !== 'boolean' || typeof next.Owner !== 'boolean' ||
                (next.Active && (!validId(next.PartyId) || !Number.isInteger(next.ViewerCount) || !Number.isInteger(next.MaxViewers) || next.ViewerCount < 1 || next.ViewerCount > next.MaxViewers))) throw new Error('Party status could not be verified.');
            if (state?.PartyId !== next.PartyId || !next.Active || !next.Owner) clearLinks();
            state = next; saved = saved.filter(link => Date.parse(link.ExpiresAt) > Date.now());
            message.textContent = next.Active ? `${next.ViewerCount} of ${next.MaxViewers} viewers · ${next.Owner ? 'You own this party' : 'Participant'}. Playback remains in Private SyncPlay.` : 'Private SyncPlay is ready. This player has no party.';
            render();
        }
        function acceptLink(link) {
            // Defence in depth: even a malformed response cannot turn a bearer link into script,
            // a cross-origin destination or an unbounded retained list. Server validates exact path/base.
            const base = new URL(window.location.href), url = new URL(link.Url);
            if (url.protocol !== 'https:' || url.origin !== base.origin || url.username || url.password || url.search ||
                !url.pathname.endsWith('/PrivateSyncPlay/Watch') || !url.hash || url.href.length > 4096 ||
                !validId(link.InviteId) || !validId(link.PartyId) || link.PartyId !== state?.PartyId ||
                !Number.isFinite(Date.parse(link.ExpiresAt)) || Date.parse(link.ExpiresAt) <= Date.now()) throw new Error('Invitation response could not be verified. Check Private SyncPlay before creating another.');
            saved = [...saved.filter(item => item.InviteId !== link.InviteId), link].slice(-4); render();
        }
        async function refresh(manual = false) {
            if (!active || busy || reading) return;
            const run = version; reading = { run };
            const read = reading;
            try {
                const next = await api('WatchParty');
                if (!active || run !== version) return;
                apply(next);
                if (manual) needsReview = false;
                if (needsReview) message.textContent += ' Last write was not confirmed. Review the companion party and refresh explicitly before another write; lost invitation URLs cannot be recovered here.';
            } catch (error) {
                if (!active || run !== version) return;
                state = undefined; clearLinks(); message.textContent = error.message;
            } finally { if (reading === read) reading = undefined; if (active && run === version) controls(); }
        }
        async function mutate(action) {
            if (!active || busy || needsReview) return;
            busy = true; const run = ++version; controls();
            try { await action(() => active && run === version); }
            catch (error) {
                if (active && run === version) {
                    needsReview = true; clearLinks();
                    message.textContent = error.message + ' Review Private SyncPlay, then explicitly refresh. No automatic retry was performed.';
                }
            } finally { if (active && run === version) { busy = false; controls(); } }
        }
        form.addEventListener('submit', event => {
            event.preventDefault(); if (form.hidden) return;
            void mutate(async current => {
                const result = await api('WatchParty', { Name: form.elements.name.value.trim() });
                if (!current()) return; apply(result.Party); acceptLink(result.Invite);
                message.textContent = 'Private party and invitation created. This is not confirmation of playback or a recipient joining.';
            });
        });
        invite.addEventListener('click', () => {
            if (!state?.Owner || saved.length >= 4) return;
            const party = state.PartyId;
            void mutate(async current => { const link = await api('WatchParty/' + party + '/Invites', {}); if (current() && state?.PartyId === party) acceptLink(link); });
        });
        for (const [selector, command, warning] of [
            ['[data-watchparty-leave]', 'Leave', 'Leave this player’s party? The final signed-in member leaving ends all guest access.'],
            ['[data-watchparty-end]', 'End', 'End this party for everyone? All signed-in members and guests will be removed.']
        ]) section.querySelector(selector).addEventListener('click', () => {
            if (!state?.Active || (command === 'End' && !state.Owner) || !window.confirm(warning)) return;
            const party = state.PartyId;
            void mutate(async current => { await api('WatchParty/' + party + '/' + command, {}); if (current()) { state = undefined; clearLinks(); message.textContent = command === 'End' ? 'Party ended. Refresh its state.' : 'Leave accepted. Refresh its state.'; } });
        });
        refreshButton.addEventListener('click', () => void refresh(true));
        function ownerButton(label, action) {
            const b = document.createElement('button', 'emby-button'); b.setAttribute('is', 'emby-button'); b.className = 'raised emby-button'; b.type = 'button'; b.textContent = label;
            b.addEventListener('click', () => void mutate(action)); return b;
        }
        function choice(parent, id, label, value, options) {
            const c = document.createElement('div'); c.className = 'selectContainer'; const attributes = document.createElement('select'); attributes.setAttribute('label', label); attributes.id = 'hubParty-' + id;
            c.insertAdjacentHTML('beforeend', attributes.outerHTML.replace('<select', '<select is="emby-select"')); const s = c.querySelector('select');
            for (const [key, text] of options) { const o = document.createElement('option'); o.value = key; o.textContent = text; s.append(o); } s.value = String(value); parent.append(c); return s;
        }
        function drawManagement(snapshot) {
            if (snapshot.PartyId !== state?.PartyId || !state.Owner || !/^[0-9A-Fa-f]{64}$/.test(snapshot.Revision)) throw new Error('Owner snapshot changed. Reload controls.');
            management.replaceChildren(); const policy = document.createElement('div'); policy.className = 'verticalSection'; const p = document.createElement('p'); p.textContent = 'Owner controls affect this party only. Account permissions stay within Jellyfin and companion limits. Guest revocation affects everyone using the shared invitation. Reload after each change; nothing here confirms playback.'; policy.append(p);
            const joins = choice(policy, 'joins', 'New viewer admission', snapshot.Policy.AllowJoins, [['true', 'Allow new viewers'], ['false', 'Pause new joins · keep existing viewers']]);
            const playback = choice(policy, 'playback', 'Participant playback control ceiling', snapshot.Policy.ParticipantsControlPlayback, [['false', 'Owner controls playback'], ['true', 'Allow permitted accounts to control playback']]);
            const queue = choice(policy, 'queue', 'Participant queue editing ceiling', snapshot.Policy.ParticipantsEditQueue, [['false', 'Owner edits queue'], ['true', 'Allow permitted accounts to edit queue']]);
            const guests = choice(policy, 'guests', 'Existing companion guest policy', 'keep', [['keep', 'Keep current companion guest policy'], ['disable', 'Disable guests and revoke all guest-enabled links']]);
            const cap = document.createElement('div'); cap.className = 'inputContainer'; cap.insertAdjacentHTML('beforeend', '<input is="emby-input" label="Maximum concurrent party viewers" id="hubParty-capacity" type="number" />'); const max = cap.querySelector('input'); max.min = String(snapshot.ViewerCount); max.max = '10000'; max.step = '1'; max.value = snapshot.Policy.MaxViewers; policy.append(cap);
            const base = 'WatchParty/' + snapshot.PartyId;
            policy.append(ownerButton('Save this party policy', async current => {
                if (!max.checkValidity()) { max.reportValidity(); return; }
                if (!window.confirm(guests.value === 'disable' ? 'Save policy and permanently revoke all guest-enabled invitations and their guest leases?' : 'Save this party policy?')) return;
                await api(base + '/Policy', { Revision: snapshot.Revision, AllowJoins: joins.value === 'true', ControlPlayback: playback.value === 'true', EditQueue: queue.value === 'true', MaxViewers: Number(max.value), DisableGuests: guests.value === 'disable' });
                if (current()) { management.replaceChildren(); message.textContent = 'Party policy accepted. Reload owner controls to inspect current state.'; }
            })); management.append(policy);
            for (const account of snapshot.Accounts) {
                const row = document.createElement('div'); row.className = 'verticalSection'; const label = document.createElement('p'); label.textContent = account.Name + ' · ' + account.SessionCount + ' active devices' + (account.IsOwner ? ' · owner' : ''); row.append(label);
                if (!account.IsOwner) {
                    const level = choice(row, account.ParticipantId, 'Account playback and queue permissions', account.Permissions.EditQueue ? 'queue' : account.Permissions.ControlPlayback ? 'playback' : 'view', [['view', 'Watch only'], ['playback', 'Control playback'], ['queue', 'Control playback and edit queue']]);
                    row.append(ownerButton('Save account permissions', async current => { await api(base + '/Accounts/' + account.ParticipantId + '/Permissions', { Revision: snapshot.Revision, ControlPlayback: level.value !== 'view', EditQueue: level.value === 'queue' }); if (current()) { management.replaceChildren(); message.textContent = 'Permissions accepted. Reload to inspect effective permissions.'; } }), ownerButton('Remove account and block rejoining this party', async current => {
                        if (!window.confirm('Remove all devices of ' + account.Name + ' and block rejoining this party? This does not ban the Jellyfin account.')) return;
                        await api(base + '/Participants/' + account.ParticipantId + '/Moderate', { Revision: snapshot.Revision, Action: 'kick-account' }); if (current()) management.replaceChildren();
                    }));
                } management.append(row);
            }
            for (const [rows, action] of [[snapshot.Guests, 'revoke-guest-link'], [snapshot.BlockedAccounts, 'unblock-account']]) for (const person of rows) {
                const row = document.createElement('div'), label = document.createElement('p'); row.className = 'verticalSection'; label.textContent = person.Name; row.append(label, ownerButton(action === 'revoke-guest-link' ? 'Revoke shared guest invitation' : 'Allow this account to rejoin', async current => {
                    if (!window.confirm(action === 'revoke-guest-link' ? 'Revoke this shared invitation and disconnect ALL guests admitted by it (currently ' + person.SharedInviteGuestCount + ', possibly more by confirmation)? Anonymous identity cannot be banned across new links.' : 'Unblock this account for this party only? It will not be joined automatically.')) return;
                    await api(base + '/Participants/' + person.ParticipantId + '/Moderate', { Revision: snapshot.Revision, Action: action, ConfirmSharedGuests: action === 'revoke-guest-link' }); if (current()) management.replaceChildren();
                })); management.append(row);
            }
        }
        manage.addEventListener('click', () => { if (state?.Owner) void mutate(async current => { const snapshot = await api('WatchParty/' + state.PartyId + '/Management'); if (current()) drawManagement(snapshot); }); });
        return {
            show() { version++; active = !window.MediaHubNativeUrl; section.hidden = !active; state = undefined; needsReview = false; controls(); },
            hide() { active = false; version++; busy = false; reading = undefined; needsReview = false; state = undefined; clearLinks(); form.reset(); message.textContent = ''; controls(); },
            refresh
        };
    };
})();
