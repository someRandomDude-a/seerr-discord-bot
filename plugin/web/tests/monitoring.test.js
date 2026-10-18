import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import vm from 'node:vm';
import { JSDOM } from 'jsdom';

const id = 'a'.repeat(48), requester = { Id: '11111111-2222-3333-4444-555555555555', Name: '<img src=x> Private viewer', Exists: true, Disabled: false };
const proposal = { Id: id, Item: requester.Id, Title: '<script>Movie</script>', Requester: requester, RequestId: 17, State: 'queued', Created: '2026-10-18T01:00:00Z', EligibleSince: '2026-10-18T02:00:00Z', DueAt: '2026-10-20T02:00:00Z', DelayDays: 2, Revision: 3, Integrity: 'verified', Checkpoint: 'prepared' };
const listing = (extra = {}) => ({ Items: [proposal], Page: 1, PageSize: 25, Total: 1, Counts: { queued: 1 }, Accounts: [requester], GeneratedAt: proposal.Created, AutomaticDeletion: false, HistoricalTransitionsAvailable: false, ...extra });
const evidence = () => ({ Proposal: proposal, Target: 'target-pin', Proof: null, ProofIntegrity: 'not recorded', CheckpointUpdated: null, Activity: [{ Account: requester, LastActivity: proposal.Created }], ActivityVerified: true, Votes: [{ Account: requester, Activity: proposal.Created, Approved: true }], Notices: [], HistoricalTransitionsAvailable: false });
async function harness(activity = false) {
    const html = await readFile(new URL('../../src/Jellyfin.Plugin.MediaHub/Web/hub.html', import.meta.url), 'utf8');
    const code = await readFile(new URL('../../src/Jellyfin.Plugin.MediaHub/Web/monitoring.js', import.meta.url), 'utf8');
    const dom = new JSDOM(html, { url: 'https://jellyfin.example/base/web/index.html' });
    const view = dom.window.document.querySelector('#mediaHubPage'), calls = [];
    let response = async path => path.includes('?') ? listing() : evidence();
    const api = async (path, body) => { calls.push({ path, body }); return response(path, body); };
    if (activity) dom.window.MediaHubNativeUrl = 'https://jellyfin.example/base/web/index.html';
    vm.runInContext(code, vm.createContext({ window: dom.window, document: dom.window.document, URLSearchParams, Date }));
    const ui = dom.window.JellyfinDeletionMonitoring(view, api, () => {});
    return { dom, view, calls, ui, form: view.querySelector('[data-monitor-filters]'), settle: () => new Promise(resolve => setImmediate(resolve)), reply(fn) { response = fn; }, close() { dom.window.close(); } };
}
test('administrator monitoring is explicitly activated, passive, text-safe and not persisted', async () => {
    const h = await harness();
    try {
        assert.equal(h.calls.length, 0); assert.equal(h.view.querySelector('[data-monitoring]').hidden, true);
        h.ui.show(); await h.settle(); assert.equal(h.calls.length, 1);
        assert.match(h.view.querySelector('[data-monitor-list]').textContent, /Private viewer/);
        assert.equal(h.view.querySelector('[data-monitor-list] img'), null); assert.equal(h.view.querySelector('[data-monitor-list] script'), null);
        h.view.querySelector('[data-monitor-list] button').click(); await h.settle();
        const detail = h.view.querySelector('[data-monitor-detail]'); assert.match(detail.textContent, /recorded votes/i); assert.match(detail.textContent, /not a complete transition timeline/);
        assert.equal(h.dom.window.document.activeElement.tagName, 'H3'); assert.equal(h.calls.filter(c => c.body).length, 0);
        assert.equal(h.dom.window.localStorage.length, 0); assert.equal(h.dom.window.sessionStorage.length, 0);
        h.ui.hide(); assert.equal(detail.childElementCount, 0); assert.doesNotMatch(h.view.querySelector('[data-monitoring]').textContent, /Private viewer/);
    } finally { h.close(); }
});
test('history filters, identity filters and pagination use bounded read endpoints only', async () => {
    const h = await harness();
    try {
        h.reply(async () => listing({ Total: 28 })); h.ui.show(); await h.settle();
        [...h.view.querySelectorAll('[data-monitor-paging] button')].find(b => b.textContent === 'Next').click(); await h.settle();
        assert.match(h.calls.at(-1).path, /page=2/);
        h.form.elements.state.value = 'history'; h.form.elements.owner.value = requester.Id; h.form.elements.query.value = '17';
        h.form.dispatchEvent(new h.dom.window.Event('submit', { cancelable: true })); await h.settle();
        const query = new URL('https://jellyfin.example/' + h.calls.at(-1).path).searchParams;
        assert.equal(query.get('state'), 'history'); assert.equal(query.get('owner'), requester.Id); assert.equal(query.get('query'), '17'); assert.equal(query.get('page'), '1');
        assert.equal(h.calls.filter(c => c.body).length, 0);
    } finally { h.close(); }
});
test('monitor polling is opt-in and authentication loss clears all private evidence', async () => {
    const h = await harness();
    try {
        h.ui.show(); await h.settle(); await h.ui.refresh(); assert.equal(h.calls.length, 1);
        h.view.querySelector('[data-monitor-list] button').click(); await h.settle(); h.form.elements.live.checked = true;
        h.reply(async () => { const error = new Error('Access revoked'); error.status = 403; throw error; });
        await h.ui.refresh(); assert.equal(h.view.querySelector('[data-monitoring]').hidden, true);
        assert.equal(h.view.querySelector('[data-monitor-detail]').childElementCount, 0); assert.doesNotMatch(h.view.querySelector('[data-monitoring]').textContent, /Private viewer/);
    } finally { h.close(); }
});
test('late evidence cannot repopulate after account or settings teardown', async () => {
    const h = await harness();
    try {
        h.ui.show(); await h.settle(); let finish;
        h.reply(async () => new Promise(resolve => { finish = resolve; })); h.view.querySelector('[data-monitor-list] button').click();
        h.view.dispatchEvent(new h.dom.window.Event('mediahubsettingshidden')); finish(evidence()); await h.settle();
        assert.equal(h.view.querySelector('[data-monitor-detail]').childElementCount, 0); assert.equal(h.view.querySelector('[data-monitoring]').hidden, true);
    } finally { h.close(); }
});
test('Activity cannot activate the administrator workspace', async () => {
    const h = await harness(true);
    try { h.ui.show(); await h.settle(); assert.equal(h.calls.length, 0); assert.equal(h.view.querySelector('[data-monitoring]').hidden, true); }
    finally { h.close(); }
});
test('authenticated history pages preserve legacy gaps and render event identities as text only', async () => {
    const h = await harness();
    try {
        h.reply(async path => {
            if (!path.includes('/History?')) return path.includes('?') ? listing() : evidence();
            return { Items: [{ Sequence: 26, At: proposal.Created, Kind: 'consent_no', State: 'waiting', Actor: requester, Reason: '<script>unavailable</script>' }], Page: 1, PageSize: 25, Total: 26, CompleteFromCreation: false, Integrity: 'verified' };
        });
        h.ui.show(); await h.settle(); h.view.querySelector('[data-monitor-list] button').click(); await h.settle();
        const detail = h.view.querySelector('[data-monitor-detail]');
        [...detail.querySelectorAll('button')].find(b => b.textContent === 'Browse authenticated history').click(); await h.settle();
        assert.match(h.calls.at(-1).path, /\/History\?page=1$/);
        assert.match(detail.textContent, /Earlier events are unavailable, not reconstructed/);
        assert.match(detail.textContent, /consent_no/); assert.match(detail.textContent, /26 retained events/);
        assert.equal(detail.querySelector('img,script'), null);
        [...detail.querySelectorAll('button')].find(b => b.textContent === 'Older events').click(); await h.settle();
        assert.match(h.calls.at(-1).path, /\/History\?page=2$/); assert.equal(h.calls.filter(c => c.body).length, 0);
    } finally { h.close(); }
});
test('late journal results cannot restore private history after teardown and access loss clears it', async () => {
    const h = await harness();
    try {
        h.ui.show(); await h.settle(); h.view.querySelector('[data-monitor-list] button').click(); await h.settle();
        const detail = h.view.querySelector('[data-monitor-detail]'); let finish;
        h.reply(async () => new Promise(resolve => { finish = resolve; }));
        [...detail.querySelectorAll('button')].find(b => b.textContent === 'Browse authenticated history').click();
        h.ui.hide(); finish({ Items: [], Page: 1, PageSize: 25, Total: 0, Integrity: 'verified' }); await h.settle();
        assert.equal(detail.childElementCount, 0);
        h.reply(async path => path.includes('?') ? listing() : evidence()); h.ui.show(); await h.settle();
        h.view.querySelector('[data-monitor-list] button').click(); await h.settle();
        h.reply(async () => { const error = new Error('Access revoked'); error.status = 403; throw error; });
        [...detail.querySelectorAll('button')].find(b => b.textContent === 'Browse authenticated history').click(); await h.settle();
        assert.equal(h.view.querySelector('[data-monitoring]').hidden, true); assert.equal(detail.childElementCount, 0);
        assert.doesNotMatch(h.view.querySelector('[data-monitoring]').textContent, /Private viewer/);
    } finally { h.close(); }
});
