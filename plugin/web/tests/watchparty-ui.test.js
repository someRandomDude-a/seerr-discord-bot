import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import vm from 'node:vm';
import { JSDOM } from 'jsdom';

const party = '11111111-2222-3333-4444-555555555555';
const invite = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee';
const empty = { Available: true, Active: false, PartyId: null, Owner: false, ViewerCount: 0, MaxViewers: 0 };
const owner = { Available: true, Active: true, PartyId: party, Owner: true, ViewerCount: 1, MaxViewers: 4 };
const management = () => ({ PartyId: party, Revision: 'a'.repeat(64), ViewerCount: 2, Policy: { AllowJoins: true, AllowGuests: false, ParticipantsControlPlayback: true, ParticipantsEditQueue: false, MaxViewers: 4 },
  Accounts: [{ ParticipantId: '99999999-2222-3333-4444-555555555555', Name: 'Owner', SessionCount: 1, IsOwner: true, Permissions: { ControlPlayback: true, EditQueue: true } }, { ParticipantId: '88888888-2222-3333-4444-555555555555', Name: '<img src=x> Viewer', SessionCount: 1, IsOwner: false, Permissions: { ControlPlayback: false, EditQueue: false } }], Guests: [], BlockedAccounts: [] });
const link = () => ({ InviteId: invite, PartyId: party, Url: 'https://jellyfin.example/base/PrivateSyncPlay/Watch#secret-bearer-token', ExpiresAt: new Date(Date.now() + 600000).toISOString() });
async function harness(activity = false) {
  const html = await readFile(new URL('../../src/Jellyfin.Plugin.MediaHub/Web/hub.html', import.meta.url), 'utf8');
  const script = await readFile(new URL('../../src/Jellyfin.Plugin.MediaHub/Web/watchparty.js', import.meta.url), 'utf8');
  const dom = new JSDOM(html, { url: 'https://jellyfin.example/base/web/index.html' });
  const view = dom.window.document.querySelector('#mediaHubPage'), calls = [];
  let response = async (_, body) => body ? { Party: owner, Invite: link() } : empty;
  const api = async (path, body) => { calls.push({ path, body }); return response(path, body); };
  if (activity) dom.window.MediaHubNativeUrl = 'https://jellyfin.example/base/web/index.html';
  vm.runInContext(script, vm.createContext({ window: dom.window, document: dom.window.document, URL, Date }));
  const ui = dom.window.JellyfinMediaWatchParty(view, api); ui.show(); await ui.refresh();
  const settle = () => new Promise(resolve => setImmediate(resolve));
  return { dom, view, calls, ui, settle, reply(fn) { response = fn; }, close() { dom.window.close(); } };
}

test('provider reads are passive; creation explicitly returns a bounded bearer link without claiming playback', async () => {
  const h = await harness();
  try {
    assert.equal(h.calls.filter(c => c.body).length, 0);
    const form = h.view.querySelector('[data-watchparty-create]'); assert.equal(form.hidden, false);
    form.elements.name.value = '<img src=x onerror=alert(1)>';
    form.dispatchEvent(new h.dom.window.Event('submit', { cancelable: true })); await h.settle();
    assert.equal(h.calls.filter(c => c.body).length, 1);
    assert.equal(h.calls.at(-1).path, 'WatchParty');
    assert.equal(h.view.querySelector('[data-watchparty-links] img'), null);
    const a = h.view.querySelector('[data-watchparty-links] a'); assert.equal(a.href, link().Url);
    assert.equal(a.rel, 'noreferrer noopener');
    assert.match(h.view.querySelector('[data-watchparty-status]').textContent, /not confirmation of playback/);
    assert.doesNotMatch(h.dom.window.location.href, /secret-bearer-token/);
    assert.equal(h.dom.window.localStorage.length, 0); assert.equal(h.dom.window.sessionStorage.length, 0);
    h.ui.hide(); assert.equal(h.view.querySelector('[data-watchparty-links] a'), null);
  } finally { h.close(); }
});

test('owner moderation loads explicitly, renders names as text, and sends an exact reviewed party revision', async () => {
  const h = await harness();
  try {
    h.reply(async path => path.endsWith('/Management') ? management() : owner); await h.ui.refresh();
    assert.equal(h.calls.some(c => c.path.endsWith('/Management')), false);
    h.view.querySelector('[data-watchparty-manage]').click(); await h.settle();
    const panel = h.view.querySelector('[data-watchparty-management]'); assert.match(panel.textContent, /<img src=x>/); assert.equal(panel.querySelector('img'), null);
    assert.equal(panel.querySelectorAll('select[label="Account playback and queue permissions"]').length, 1);
    h.dom.window.confirm = () => false;
    [...panel.querySelectorAll('button')].find(b => b.textContent === 'Save this party policy').click(); await h.settle(); assert.equal(h.calls.some(c => c.body), false);
    h.dom.window.confirm = () => true;
    [...panel.querySelectorAll('button')].find(b => b.textContent === 'Save this party policy').click(); await h.settle();
    assert.equal(h.calls.at(-1).path, 'WatchParty/' + party + '/Policy'); assert.equal(h.calls.at(-1).body.Revision, 'a'.repeat(64)); assert.equal(h.calls.at(-1).body.DisableGuests, false);
    assert.equal(panel.childElementCount, 0);
  } finally { h.close(); }
});

test('owner management private participants clear on ownership loss and navigation', async () => {
  const h = await harness();
  try {
    h.reply(async path => path.endsWith('/Management') ? management() : owner); await h.ui.refresh(); h.view.querySelector('[data-watchparty-manage]').click(); await h.settle();
    assert.match(h.view.querySelector('[data-watchparty-management]').textContent, /Viewer/);
    h.reply(async () => ({ ...owner, Owner: false })); await h.ui.refresh();
    assert.equal(h.view.querySelector('[data-watchparty-manage]').hidden, true); assert.equal(h.view.querySelector('[data-watchparty-management]').childElementCount, 0);
    h.ui.hide(); assert.doesNotMatch(h.view.querySelector('[data-watchparty-management]').textContent, /Viewer/);
  } finally { h.close(); }
});

test('uncertain writes are not replayed by polling or another submit; explicit refresh is required', async () => {
  const h = await harness();
  try {
    h.reply(async (_, body) => { if (body) throw new Error('Not confirmed.'); return owner; });
    const form = h.view.querySelector('[data-watchparty-create]');
    form.dispatchEvent(new h.dom.window.Event('submit', { cancelable: true })); await h.settle();
    await h.ui.refresh();
    form.dispatchEvent(new h.dom.window.Event('submit', { cancelable: true })); await h.settle();
    assert.equal(h.calls.filter(c => c.body).length, 1);
    assert.equal(h.view.querySelector('[data-watchparty-invite]').disabled, true);
    assert.match(h.view.querySelector('[data-watchparty-status]').textContent, /Last write was not confirmed/);
    h.view.querySelector('[data-watchparty-refresh]').click(); await h.settle();
    assert.equal(h.view.querySelector('[data-watchparty-invite]').disabled, false);
  } finally { h.close(); }
});

test('leaving and ending require explicit confirmation and a participant never gets owner actions', async () => {
  const h = await harness();
  try {
    h.reply(async () => owner); await h.ui.refresh();
    h.dom.window.confirm = () => false;
    h.view.querySelector('[data-watchparty-end]').click(); await h.settle(); assert.equal(h.calls.filter(c => c.body).length, 0);
    h.dom.window.confirm = () => true;
    h.reply(async (_, body) => body ? { Ended: true } : owner);
    h.view.querySelector('[data-watchparty-end]').click(); await h.settle();
    assert.equal(h.calls.at(-1).path, 'WatchParty/' + party + '/End');
    h.reply(async () => ({ ...owner, Owner: false })); await h.ui.refresh();
    assert.equal(h.view.querySelector('[data-watchparty-invite]').hidden, true);
    assert.equal(h.view.querySelector('[data-watchparty-end]').hidden, true);
  } finally { h.close(); }
});

test('late creation cannot disclose a link after page/identity teardown and remount', async () => {
  const h = await harness();
  try {
    let finish; h.reply(async (_, body) => body ? new Promise(resolve => { finish = resolve; }) : empty);
    h.view.querySelector('[data-watchparty-create]').dispatchEvent(new h.dom.window.Event('submit', { cancelable: true }));
    h.ui.hide(); h.ui.show(); await h.ui.refresh();
    finish({ Party: owner, Invite: link() }); await h.settle();
    assert.equal(h.view.querySelector('[data-watchparty-links] a'), null);
    assert.equal(h.view.querySelector('[data-watchparty-create]').hidden, false);
  } finally { h.close(); }
});

test('Activity cannot call native watch-party operations or show its controls', async () => {
  const h = await harness(true);
  try {
    assert.equal(h.view.querySelector('[data-watchparty]').hidden, true);
    assert.equal(h.calls.length, 0);
  } finally { h.close(); }
});

test('malformed party IDs and malicious link responses never become active navigation', async () => {
  const h = await harness();
  try {
    h.reply(async () => ({ ...owner, PartyId: '../../Settings' })); await h.ui.refresh();
    assert.equal(h.view.querySelector('[data-watchparty-actions]').hidden, true);
    h.reply(async (_, body) => body ? { Party: owner, Invite: { ...link(), Url: 'javascript:alert(1)' } } : empty);
    await h.ui.refresh();
    h.view.querySelector('[data-watchparty-create]').dispatchEvent(new h.dom.window.Event('submit', { cancelable: true })); await h.settle();
    assert.equal(h.view.querySelector('[data-watchparty-links] a'), null);
    assert.equal(h.view.querySelector('[data-watchparty-invite]').disabled, true);
  } finally { h.close(); }
});
