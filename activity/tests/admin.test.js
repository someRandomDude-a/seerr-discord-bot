import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import vm from 'node:vm';
import { JSDOM } from 'jsdom';

const guild = '1234567890123456789';
const channel = '1234567890123456790';
const user = '1234567890123456791';
const state = {
  guilds: [{ id: guild, name: 'Cinema', channel: 'general' }],
  threads: [{ kind: 'dm', id: user, name: 'Viewer', count: 1 }, { kind: 'guild', id: guild, name: 'Cinema', count: 1 }],
  jobs: [], all_users_enabled: true, retention_days: 7, max_messages: 10000,
};
const inbox = {
  messages: [{ message_id: '1234567890123456792', author_id: user, author_name: 'Viewer', guild_id: null, channel_id: channel, content: '<img src=x onerror=alert(1)>', attachment_count: 0, created_at: 1 }],
  total: 1, page: 1, channels: [{ channel_id: channel, channel_name: 'general', guild_id: guild }],
  users: [{ author_id: user, author_name: 'Viewer' }],
};

async function harness(isAdmin = true, received = inbox) {
  const dom = new JSDOM('<div id="app"></div>');
  dom.window.HTMLDialogElement.prototype.showModal = function () { this.open = true; };
  dom.window.HTMLDialogElement.prototype.close = function () { this.open = false; };
  const calls = [];
  const context = vm.createContext({
    document: dom.window.document, console, crypto: { randomUUID: () => 'state' },
    setInterval: () => 1, clearInterval: () => {},
    DiscordSDK: class {
      guildId = guild;
      ready = async () => {};
      commands = { authorize: async () => ({ code: 'mock' }), authenticate: async () => {} };
    },
    fetch: async (path, options) => {
      const args = options.body ? JSON.parse(options.body) : {};
      calls.push({ path, ...args });
      let data;
      if (path.endsWith('/config')) data = { application_id: '123', scopes: ['identify'], refresh_interval: 60 };
      else if (path.endsWith('/auth')) data = { access_token: 'mock', session_token: 'mock' };
      else if (args.operation === 'profile') data = { account: null, devices: [], is_admin: isAdmin };
      else if (args.operation === 'admin_state') data = { result: state };
      else if (args.operation === 'admin_inbox') data = { result: received };
      else if (args.operation === 'admin_prepare') data = { result: { plan: 'preview', channels: args.user_id ? 0 : 1, users: args.user_id ? 1 : 0, skipped: [], destinations: ['Cinema / #general'] } };
      else if (args.operation === 'admin_send') data = { result: { job: 1 } };
      else throw new Error(`Unexpected operation: ${args.operation}`);
      return { ok: true, json: async () => data };
    },
  });
  // Run the production DOM/rendering/event code, substituting only module imports and SDK/network.
  const domSource = (await readFile(new URL('../src/dom.js', import.meta.url), 'utf8')).replaceAll('export function', 'function');
  const main = (await readFile(new URL('../src/main.js', import.meta.url), 'utf8'))
    .replace(/^import .*;\r?\n/gm, '')
    .replace('boot().catch(error => showError(error));', 'globalThis.ready = boot();');
  vm.runInContext(domSource + '\n' + main + '\nglobalThis.testUI = { openAdmin: async () => { page = "admin"; await loadPage(); } };', context);
  await context.ready;
  const settle = async () => { for (let i = 0; i < 8; i++) await new Promise(resolve => setImmediate(resolve)); };
  return { document: dom.window.document, window: dom.window, calls, context, settle, close: () => dom.window.close() };
}

function click(document, text) {
  const button = [...document.querySelectorAll('button')].find(element => element.textContent === text);
  assert.ok(button, `Missing button: ${text}`);
  button.click();
}

test('operators get a grouped inbox without a Seerr link; content is rendered safely', async () => {
  const h = await harness();
  try {
    assert.ok(h.document.querySelector('[data-page="admin"]'));
    await h.context.testUI.openAdmin();
    assert.equal(h.document.querySelector('.guild-checklist input').checked, true);
    assert.equal(h.document.querySelectorAll('.thread-button').length, 2);
    assert.equal(h.document.querySelector('.received-content').textContent, '<img src=x onerror=alert(1)>');
    assert.equal(h.document.querySelector('.received-content img'), null);
    const type = h.document.querySelector('.inbox-filters select');
    type.value = 'dm'; type.dispatchEvent(new h.window.Event('change')); await h.settle();
    assert.equal(h.calls.filter(call => call.operation === 'admin_inbox').at(-1).kind, 'dm');
    assert.equal(h.document.querySelectorAll('.thread-button').length, 1);
    assert.equal(h.document.querySelector('[aria-label="Filter by server"]').disabled, true);
    const query = h.document.querySelector('.inbox-search input'); query.value = 'Film';
    h.document.querySelector('.inbox-search').dispatchEvent(new h.window.Event('submit', { cancelable: true })); await h.settle();
    assert.equal(h.calls.filter(call => call.operation === 'admin_inbox').at(-1).query, 'Film');
    click(h.document, 'Reply');
    assert.equal(h.document.querySelector('.compose>input').value, user);
    assert.equal(h.document.querySelector('.guild-checklist input').checked, false);
  } finally { h.close(); }
});

test('server replies select the original channel rather than a global broadcast', async () => {
  const received = structuredClone(inbox);
  received.messages[0].guild_id = guild;
  received.messages[0].guild_name = 'Cinema';
  received.messages[0].channel_name = 'general';
  const h = await harness(true, received);
  try {
    await h.context.testUI.openAdmin();
    click(h.document, 'Reply');
    assert.equal(h.document.querySelector('.compose>input').value, '');
    assert.equal(h.document.querySelector('.guild-checklist input').checked, true);
    const message = h.document.querySelector('textarea'); message.value = 'A reply'; message.dispatchEvent(new h.window.Event('input'));
    click(h.document, 'Preview send'); await h.settle();
    const prepare = h.calls.find(call => call.operation === 'admin_prepare');
    assert.equal(prepare.channel_id, channel);
    assert.deepEqual(prepare.guild_ids, [guild]);
    assert.equal(prepare.all_users, false);
    click(h.document, 'Cancel');
    assert.equal(h.calls.some(call => call.operation === 'admin_send'), false);
  } finally { h.close(); }
});

test('messages stay hidden from non-operators', async () => {
  const h = await harness(false);
  try { assert.equal(h.document.querySelector('[data-page="admin"]'), null); }
  finally { h.close(); }
});

test('send preview preserves snowflake strings and sends only after confirmation', async () => {
  const h = await harness();
  try {
    await h.context.testUI.openAdmin();
    const message = h.document.querySelector('textarea');
    message.value = 'A short update'; message.dispatchEvent(new h.window.Event('input'));
    click(h.document, 'Preview send'); await h.settle();
    const prepare = h.calls.find(call => call.operation === 'admin_prepare');
    assert.deepEqual(prepare.guild_ids, [guild]);
    assert.equal(prepare.message, 'A short update');
    assert.equal(h.calls.some(call => call.operation === 'admin_send'), false);
    assert.ok(h.document.querySelector('dialog').textContent.includes('Cinema / #general'));
    click(h.document, 'Confirm'); await h.settle();
    assert.equal(h.calls.find(call => call.operation === 'admin_send').confirmed, true);
    assert.equal(h.document.querySelector('textarea').value, '');
  } finally { h.close(); }
});
