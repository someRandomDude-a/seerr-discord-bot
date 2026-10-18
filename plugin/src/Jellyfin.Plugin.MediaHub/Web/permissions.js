(function () {
    'use strict';
    window.JellyfinDeletionPermissions = function (view, api, verify) {
        const section = view.querySelector('[data-deletion-permissions]'), form = section.querySelector('form');
        const notice = section.querySelector('[data-deletion-permission-notice]'), rows = section.querySelector('[data-deletion-overrides]');
        const save = form.querySelector('[type="submit"]');
        let active = false, generation = 0, revision, accounts = [], overrides = new Map(), busy = false, uncertain = false;
        const labels = ['Cannot request deletion', 'Own approved requests', 'Any accessible supported item'];
        function hide() { active = false; generation++; revision = undefined; accounts = []; overrides.clear(); rows.replaceChildren(); form.reset(); form.elements.account.replaceChildren(); notice.textContent = ''; section.hidden = true; busy = false; uncertain = false; }
        function controls() { save.disabled = busy || uncertain || revision === undefined; form.querySelector('[data-deletion-set-override]').disabled = busy || revision === undefined; }
        function draw() {
            rows.replaceChildren();
            for (const [id, scope] of overrides) { const row = document.createElement('li'); row.className = 'listItem'; row.textContent = `${accounts.find(a => a.Id === id)?.Name || 'Unavailable account'} · ${id} · ${labels[scope]}`; rows.append(row); }
            form.elements.scope.value = String(overrides.get(form.elements.account.value) ?? 'inherit'); controls();
        }
        async function load() {
            if (!active || busy) return; const run = generation; busy = true; controls();
            try {
                verify(); const value = await api('Admin/DeletionPermissions'); verify(); if (!active || run !== generation) return;
                revision = value.Revision; accounts = value.Accounts; overrides = new Map((value.Permissions.Users || []).map(row => [row.User, row.Scope]));
                form.elements.defaultScope.value = String(value.Permissions.Default);
                form.elements.account.replaceChildren();
                for (const user of accounts) { const option = document.createElement('option'); option.value = user.Id; option.textContent = `${user.Name}${user.Disabled ? ' · disabled' : ''}`; form.elements.account.append(option); }
                uncertain = false; notice.textContent = 'Saved permissions loaded. Editing does not save; account choices must be applied to the draft.'; draw();
            } catch (error) { if (run === generation) { hide(); notice.textContent = error.message; } }
            finally { if (run === generation) { busy = false; controls(); } }
        }
        form.elements.account.addEventListener('change', draw);
        form.querySelector('[data-deletion-set-override]').addEventListener('click', () => {
            if (!active || busy || revision === undefined || !form.elements.account.value) return; verify();
            const id = form.elements.account.value, scope = form.elements.scope.value;
            if (scope === 'inherit') overrides.delete(id); else overrides.set(id, Number(scope));
            notice.textContent = 'Unsaved permission draft. Save explicitly to apply it.'; draw();
        });
        form.querySelector('[data-deletion-reload-permissions]').addEventListener('click', () => { if (active && window.confirm('Discard this permission draft and reload saved permissions?')) void load(); });
        form.addEventListener('submit', async event => {
            event.preventDefault(); if (!active || busy || uncertain || revision === undefined) return;
            verify(); const policy = { Default: Number(form.elements.defaultScope.value), Users: [...overrides].map(([User, Scope]) => ({ User, Scope })) };
            if (!window.confirm('Save these deletion request permissions? Pending proposals will be rechecked. This never bypasses consent, delay or target verification.')) return;
            const run = generation; busy = true; controls();
            try {
                const saved = await api('Admin/DeletionPermissions', { Revision: revision, Permissions: policy }); verify(); if (!active || run !== generation) return;
                revision = saved.Revision; notice.textContent = 'Permissions saved. Reload other server settings forms before saving them; their revisions may now be stale.';
            } catch (error) { if (run === generation) { uncertain = true; notice.textContent = error.message + ' Reload saved permissions before another save; no automatic retry was sent.'; } }
            finally { if (run === generation) { busy = false; controls(); } }
        });
        view.addEventListener('mediahubsettingshidden', hide);
        return { show() { if (window.MediaHubNativeUrl || active) return; verify(); active = true; section.hidden = false; void load(); }, hide };
    };
})();
