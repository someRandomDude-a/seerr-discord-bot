import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import vm from 'node:vm';
import { JSDOM } from 'jsdom';

async function harness(operator = true) {
  const html = await readFile(new URL('../../src/Jellyfin.Plugin.MediaHub/Web/hub.html', import.meta.url), 'utf8');
  const source = await readFile(new URL('../../src/Jellyfin.Plugin.MediaHub/Web/messenger.js', import.meta.url), 'utf8');
  const dom = new JSDOM(html, { pretendToBeVisual: true, url: 'https://jellyfin.example/base/web/index.html' });
  const view = dom.window.document.querySelector('#mediaHubPage'), calls = [], timers = [], control = {};
  let nonce = 0;
  Object.defineProperty(dom.window.crypto, 'randomUUID', { value: () => 'unique-send-nonce-' + (++nonce) });
  const message = { Id: '100', AuthorName: '<script>sender</script>', Content: '<img src=x onerror=alert(1)>', Rich: { embeds: [{ title: '<img src=x>', description: 'Description', fields: [{ name: 'Name', value: '<script>x</script>' }] }], attachments: [{ key: 'attachment-0', filename: 'file.txt' }] }, Created: '2026-10-16T00:00:00Z', Direction: 'in' };
  const api = async (path, body) => {
    calls.push({ path, body });
    if (control.reply) { const reply = await control.reply(path, body); if (reply !== undefined) return reply; }
    if (path === 'Messaging/Access') return { Operator: operator };
    if (path === 'Messaging/Destinations') return [{ Target: { Kind: 'channel', Id: '123', Guild: '555' }, Label: 'Allowed / #channel' }];
    if (path.startsWith('Messaging/Inbox?')) return { Messages: [message], Page: 1, HasNext: false };
    if (path === 'Messaging/History') return [];
    if (path === 'Messaging/Prepare') return { Id: 'plan', Content: body.Content, Destinations: [{ Label: 'Verified target' }] };
    return { Id: 'job', Mode: 'normal', Content: body?.Content || '', Created: '2026-10-16T00:00:00Z', Deliveries: [{ Destination: 'Verified target', State: 'pending' }] };
  };
  const client = { accessToken: () => 'native-token', getUrl: path => '/base/' + path };
  vm.runInContext(source, vm.createContext({ window: dom.window, document: dom.window.document, setInterval: fn => { timers.push(fn); return fn; }, clearInterval() {}, ApiClient: client }));
  const panel = dom.window.JellyfinMediaMessenger(view, api), settle = () => new Promise(resolve => setImmediate(resolve));
  await panel.show(); await settle();
  return { dom, view, panel, calls, control, timers, client, settle, close: () => { panel.hide(); dom.window.close(); } };
}
const submit = h => h.view.querySelector('[data-message-compose]').dispatchEvent(new h.dom.window.Event('submit', { cancelable: true }));
test('non-operators never load private inboxes or destinations', async () => {
  const h = await harness(false);
  try { assert.equal(h.view.querySelector('[data-messenger]').hidden, true); assert.deepEqual(h.calls.map(c => c.path), ['Messaging/Access']); }
  finally { h.close(); }
});

test('mention and retained-reply navigation are explicit authorized reads without automatic fetching or sending', async () => {
  const h = await harness();
  try {
    const row = { Id: '100', Author: '42', AuthorName: 'Sender', Guild: '555', Channel: '123', ChannelName: '#allowed', Content: 'hello', Rich: { mentions: [{ id: '44', name: '<img src=x>' }], reply: { id: '99', channel: '123' } } };
    h.control.reply = async path => path.startsWith('Messaging/Inbox?') ? { Messages: [row], HasNext: false } : path === 'Messaging/Inbox/99' ? { ...row, Content: 'retained referenced text', Rich: {} } : undefined;
    await h.timers[0](); await h.settle();
    assert.equal(h.calls.some(c => c.path === 'Messaging/Inbox/99'), false);
    [...h.view.querySelectorAll('[data-message-inbox] button')].find(b => b.textContent === 'View retained referenced message').click(); await h.settle();
    assert.match(h.view.querySelector('[data-message-reference]').textContent, /retained referenced text/);
    [...h.view.querySelectorAll('[data-message-inbox] button')].find(b => b.textContent.startsWith('Messages involving')).click(); await h.settle();
    assert.ok(h.calls.some(c => c.path.includes('peer=44') && c.path.includes('guild=555')));
    assert.equal(h.calls.some(c => c.body), false); assert.equal(h.view.querySelector('[data-message-inbox] img'), null);
    h.panel.hide(); assert.equal(h.view.querySelector('[data-message-reference]').childElementCount, 0);
  } finally { h.close(); }
});
test('untrusted message content and rich embeds are text, with no automatic asset fetch', async () => {
  const h = await harness();
  try {
    const inbox = h.view.querySelector('[data-message-inbox]'); assert.match(inbox.textContent, /<script>sender/); assert.match(inbox.textContent, /<img src=x/);
    assert.equal(inbox.querySelector('script,img,iframe'), null); assert.equal(h.calls.some(c => c.path.includes('/Assets/')), false);
  } finally { h.close(); }
});
test('polling preserves compose drafts and a transient failure clears private results without losing the draft', async () => {
  const h = await harness();
  try {
    const draft = h.view.querySelector('#hubMessageText'); draft.value = 'Unsent draft'; await h.timers[0](); await h.settle();
    assert.equal(h.view.querySelector('#hubMessageText'), draft); assert.equal(draft.value, 'Unsent draft');
    h.control.reply = async path => { if (path.startsWith('Messaging/Inbox?')) throw Object.assign(new Error('Unavailable'), { status: 503 }); };
    await h.timers[0](); await h.settle(); assert.equal(draft.value, 'Unsent draft'); assert.equal(h.view.querySelector('[data-message-inbox]').children.length, 0);
    assert.match(h.view.querySelector('[data-message-status]').textContent, /draft was kept/);
  } finally { h.close(); }
});
test('normal sends use a stable per-draft idempotency nonce and never auto-send on a poll', async () => {
  const h = await harness();
  try {
    const form = h.view.querySelector('[data-message-compose]'); form.elements.channels.options[0].selected = true; form.elements.content.value = 'Hello';
    submit(h); submit(h); await h.settle(); assert.equal(h.calls.filter(c => c.path === 'Messaging/Chat').length, 1);
    submit(h); await h.settle(); const writes = h.calls.filter(c => c.path === 'Messaging/Chat'); assert.equal(writes[0].body.RequestId, writes[1].body.RequestId);
    await h.timers[0](); await h.settle(); assert.equal(h.calls.filter(c => c.path === 'Messaging/Chat').length, 2);
    assert.equal(h.calls.some(c => c.path === 'Messaging/Prepare'), false);
  } finally { h.close(); }
});
test('announcements require an immutable preview and edited drafts cannot use an old confirm control', async () => {
  const h = await harness();
  try {
    const form = h.view.querySelector('[data-message-compose]'); form.elements.mode.value = 'announcement'; form.elements.channels.options[0].selected = true; form.elements.content.value = 'Original';
    submit(h); await h.settle(); assert.equal(h.calls.filter(c => c.path.endsWith('/Confirm')).length, 0);
    const confirm = h.view.querySelector('[data-message-preview] button'); assert.match(h.view.querySelector('[data-message-preview]').textContent, /Original.*Verified target/);
    form.elements.content.value = 'Changed'; form.dispatchEvent(new h.dom.window.Event('input')); confirm.click(); await h.settle(); assert.equal(h.calls.filter(c => c.path.endsWith('/Confirm')).length, 0);
    submit(h); await h.settle(); h.view.querySelector('[data-message-preview] button').click(); await h.settle();
    assert.equal(h.calls.filter(c => c.path.endsWith('/Confirm')).length, 1); assert.equal(h.calls.some(c => c.path === 'Messaging/Chat'), false);
  } finally { h.close(); }
});
test('lost operator access and navigation clear drafts, private content and controls', async () => {
  const h = await harness();
  try {
    h.view.querySelector('#hubMessageText').value = 'Private draft';
    h.control.reply = async path => { if (path.startsWith('Messaging/Inbox?')) throw Object.assign(new Error('Access lost'), { status: 403 }); };
    await h.timers[0](); await h.settle(); assert.equal(h.view.querySelector('[data-messenger]').hidden, true); assert.equal(h.view.querySelector('#hubMessageText').value, '');
    assert.equal(h.view.querySelector('[data-message-inbox]').children.length, 0); assert.equal(h.view.querySelector('[data-message-preview]').children.length, 0);
  } finally { h.close(); }
});

test('native attachment transfers use the supported Authorization header, never a legacy token header or URL token', async () => {
  const h = await harness();
  try {
    const transfers = [];
    h.dom.window.URL.createObjectURL = () => 'blob:test'; h.dom.window.URL.revokeObjectURL = () => {};
    h.dom.window.HTMLAnchorElement.prototype.click = () => {};
    h.dom.window.fetch = async (url, options) => { transfers.push({ url, options }); return { ok: true, blob: async () => new h.dom.window.Blob(['file']) }; };
    [...h.view.querySelectorAll('[data-message-inbox] button')].find(b => b.textContent === 'Download file.txt').click(); await h.settle(); await h.settle();
    assert.equal(transfers.length, 1);
    assert.match(transfers[0].url.href, /^https:\/\/jellyfin.example\/base\/MediaHub\/Messaging\/Inbox\//);
    assert.equal(transfers[0].options.headers.Authorization, 'MediaBrowser Token="native-token"');
    assert.equal(transfers[0].options.headers['X-Emby-Token'], undefined);
    assert.doesNotMatch(transfers[0].url.href, /native-token/);
    assert.equal(h.calls.at(-1).path, 'Messaging/Access');
  } finally { h.close(); }
});

test('Activity attachment adapter never requires or reads a native bearer', async () => {
  const h = await harness();
  try {
    let transferred;
    h.client.accessToken = () => { throw new Error('Native credentials must not be used by Activity.'); };
    h.client.mediaHubTransfer = async path => { transferred = path; return new h.dom.window.Blob(['file']); };
    h.dom.window.URL.createObjectURL = () => 'blob:test'; h.dom.window.URL.revokeObjectURL = () => {};
    h.dom.window.HTMLAnchorElement.prototype.click = () => {};
    [...h.view.querySelectorAll('[data-message-inbox] button')].find(b => b.textContent === 'Download file.txt').click(); await h.settle(); await h.settle();
    assert.match(transferred, /^Messaging\/Inbox\/100\/Assets\/attachment-0$/);
    assert.equal(h.calls.at(-1).path, 'Messaging/Access');
    assert.equal(h.view.querySelector('[data-message-status]').textContent, '');
  } finally { h.close(); }
});
