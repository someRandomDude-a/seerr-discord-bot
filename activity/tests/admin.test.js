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

async function harness(isAdmin = true, received = inbox, verified = true, titles = [{ kind: 'movie', external_id: '10', title: 'Private Film', available: true, poster_path: '/a.jpg', requestable: true }]) {
  const dom = new JSDOM('<div id="app"></div>');
  dom.window.HTMLDialogElement.prototype.showModal = function () { this.open = true; };
  dom.window.HTMLDialogElement.prototype.close = function () { this.open = false; };
  const calls = [];
  let isVerified = verified;
  let unavailable = false;
  const control = { delayBrowse: null, delayWrite: null, failWrite: false };
  const preferences = new Map(), stored = [];
  const viewer = () => ({ account: isVerified ? { name: 'Private Viewer', opted_in: false } : null,
    devices: isVerified ? ['Secret Server'] : [], is_admin: isVerified && isAdmin, refresh_interval: 60, sync_interval: 300 });
  const context = vm.createContext({
    document: dom.window.document, console, crypto: { randomUUID: () => 'state' },
    setInterval: () => 1, clearInterval: () => {},
    localStorage: { getItem: key => preferences.get(key), setItem: (key, value) => { preferences.set(key, value); stored.push({ key, value }); } },
    URL: { createObjectURL: () => 'blob:private-image', revokeObjectURL: () => {} },
    DiscordSDK: class {
      guildId = guild;
      ready = async () => {};
      commands = { authorize: async () => ({ code: 'mock' }), authenticate: async () => {}, openExternalLink: async ({ url }) => calls.push({ external: url }) };
    },
    fetch: async (path, options) => {
      const args = options.body ? JSON.parse(options.body) : {};
      calls.push({ path, ...args, headers: options.headers });
      if (args.operation === 'browse' && control.delayBrowse) await control.delayBrowse;
      if (args.operation === 'request') {
        if (control.delayWrite) await control.delayWrite;
        if (control.failWrite) return { ok: false, status: 400, json: async () => ({ error: 'Request could not be confirmed.' }) };
      }
      let data;
      if (path.endsWith('/config')) data = { application_id: '123', scopes: ['identify'], refresh_interval: 60 };
      else if (path.endsWith('/auth')) data = { access_token: 'mock', session_token: 'mock' };
      else if (['profile', 'browse'].includes(args.operation) && unavailable) return { ok: false, status: 400, json: async () => ({ error: 'Verification unavailable', private_data_blocked: true }) };
      else if (args.operation === 'profile') data = viewer();
      else if (args.operation === 'browse') {
        if (!isVerified) return { ok: false, status: 400, json: async () => ({ error: 'Run /link again.', private_data_blocked: true, verification_required: true }) };
        const result = args.page === 'home' ? { account: viewer().account, requests: 1, library: titles.length } : args.page === 'settings' ? null : titles;
        data = { viewer: viewer(), meta: { last_success: 1000 }, result };
      }
      else if (args.operation === 'details') data = { result: { ...args.item, overview: 'Fresh live synopsis', available: true, poster_path: '/a.jpg' } };
      else if (args.operation === 'seerr_link') data = { result: 'https://seerr.example' };
      else if (args.operation === 'sync_watchlist') data = { result: 'Movie/series watchlists synced both ways. Music and books stay hub-only.' };
      else if (args.operation === 'watch') data = { result: 'Added to watchlist.' };
      else if (args.operation === 'request') data = { result: 'Request recorded.' };
      else if (args.operation === 'dashboard') data = { result: { account: { name: 'Private Viewer', opted_in: false }, requests: 1, library: 1 } };
      else if (args.operation === 'status') data = { last_success: 1000 };
      else if (args.operation === 'library') data = { result: [{ kind: 'movie', title: 'Private Film', available: true, poster_path: '/a.jpg' }] };
      else if (path.includes('/poster/')) return { ok: true, blob: async () => ({}) };
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
  vm.runInContext(domSource + '\n' + main + '\nglobalThis.testUI = { openAdmin: async () => { page = "admin"; await loadPage(); }, openLibrary: async () => { page = "library"; await loadPage(); }, reload: loadPage };', context);
  await context.ready;
  const settle = async () => { for (let i = 0; i < 8; i++) await new Promise(resolve => setImmediate(resolve)); };
  return { document: dom.window.document, window: dom.window, calls, context, settle, control, stored,
    revoke: () => { isVerified = false; }, outage: () => { unavailable = true; }, close: () => dom.window.close() };
}

function click(document, text) {
  const button = [...document.querySelectorAll('button')].find(element => element.textContent === text);
  assert.ok(button, `Missing button: ${text}`);
  button.click();
}

test('verified operators get a grouped inbox; content is rendered safely', async () => {
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

test('unverified viewers see only verification and never load server data or images', async () => {
  const h = await harness(true, inbox, false);
  try {
    assert.ok(h.document.body.textContent.includes('Connect Jellyfin'));
    assert.equal(h.document.querySelector('[data-page="admin"]'), null);
    assert.equal(h.document.querySelector('img,a'), null);
    assert.equal(h.calls.some(call => ['browse', 'dashboard', 'status', 'library', 'admin_state'].includes(call.operation)), false);
    assert.equal(h.document.body.textContent.includes('Secret Server'), false);
  } finally { h.close(); }
});

test('a page loads through one authenticated browse call, without separate profile and status round trips', async () => {
  const h = await harness();
  try {
    const before = h.calls.length;
    await h.context.testUI.openLibrary(); await h.settle();
    const metadata = h.calls.slice(before).filter(call => call.operation);
    assert.equal(metadata.length, 1);
    assert.equal(metadata[0].operation, 'browse');
    assert.equal(metadata[0].page, 'library');
  } finally { h.close(); }
});

test('gallery paging is bounded, and filtering and position survive returning to the library', async () => {
  const titles = Array.from({ length: 30 }, (_, i) => ({ kind: 'movie', external_id: String(i + 1), title: `Film ${String(i).padStart(2, '0')}`, available: true, poster_path: '/a.jpg' }));
  const h = await harness(true, inbox, true, titles);
  try {
    await h.context.testUI.openLibrary(); await h.settle();
    assert.equal(h.document.querySelectorAll('.media-card').length, 12);
    const browseCalls = h.calls.filter(call => call.operation === 'browse').length;
    click(h.document, 'Next →'); await h.settle();
    assert.ok(h.document.querySelector('.count').textContent.includes('Page 2/3'));
    assert.equal(h.calls.filter(call => call.operation === 'browse').length, browseCalls);
    await h.context.testUI.reload();
    assert.ok(h.document.querySelector('.count').textContent.includes('Page 2/3'));
    const filter = h.document.querySelector('[aria-label="Filter collection"]');
    filter.value = 'Film 29'; filter.dispatchEvent(new h.window.Event('input'));
    assert.equal(h.document.querySelectorAll('.media-card').length, 1);
    await h.context.testUI.reload();
    assert.equal(h.document.querySelector('[aria-label="Filter collection"]').value, 'Film 29');
    assert.equal(h.document.querySelectorAll('.media-card').length, 1);
  } finally { h.close(); }
});

test('a poster opens live details and Back preserves the gallery rather than reloading it', async () => {
  const h = await harness();
  try {
    await h.context.testUI.openLibrary(); await h.settle();
    h.document.querySelector('button.cover').click(); await h.settle();
    assert.ok(h.document.querySelector('.media-detail').textContent.includes('Fresh live synopsis'));
    assert.equal(h.calls.find(call => call.operation === 'details').item.external_id, '10');
    const count = h.calls.filter(call => call.operation === 'browse').length;
    click(h.document, '← Back to browsing');
    assert.equal(h.document.querySelector('.media-detail'), null);
    assert.equal(h.calls.filter(call => call.operation === 'browse').length, count);
  } finally { h.close(); }
});

test('Discover immediately browses popular titles and opens Seerr without embedding credentials', async () => {
  const h = await harness();
  try {
    h.document.querySelector('[data-page="search"]').click(); await h.settle();
    assert.ok(h.document.body.textContent.includes('Popular now'));
    assert.ok(h.document.querySelector('.media-card'));
    click(h.document, 'Open Seerr ↗'); await h.settle();
    assert.equal(h.calls.find(call => call.external).external, 'https://seerr.example');
    assert.equal(h.document.querySelector('iframe'), null);
  } finally { h.close(); }
});

test('watchlist preferences describe automatic two-way sync and offer a sync-now action instead of import', async () => {
  const h = await harness();
  try {
    h.document.querySelector('[data-page="settings"]').click(); await h.settle();
    assert.ok(h.document.body.textContent.includes('Two-way watchlist sync'));
    assert.ok(h.document.body.textContent.includes('including removals'));
    assert.equal([...h.document.querySelectorAll('button')].some(button => button.textContent === 'Import'), false);
    click(h.document, 'Sync now'); await h.settle();
    assert.equal(h.calls.filter(call => call.operation === 'sync_watchlist').length, 1);
    assert.ok(h.document.querySelector('#notice').textContent.includes('both ways'));
  } finally { h.close(); }
});

test('posters use header authentication and revocation clears rendered private content', async () => {
  const h = await harness();
  try {
    await h.context.testUI.openLibrary(); await h.settle();
    assert.ok(h.document.body.textContent.includes('Private Film'));
    const poster = h.calls.find(call => call.path.includes('/poster/'));
    assert.equal(poster.headers.Authorization, 'Bearer mock');
    assert.equal(poster.path.includes('mock'), false);
    assert.equal(h.document.querySelector('img').src, 'blob:private-image');
    h.revoke(); await h.context.testUI.reload();
    assert.equal(h.document.body.textContent.includes('Private Film'), false);
    assert.equal(h.document.querySelector('img,a'), null);
    assert.equal(h.document.querySelector('[data-page="library"]'), null);
  } finally { h.close(); }
});

test('an identity outage clears private UI and preserves a usable retry action', async () => {
  const h = await harness();
  try {
    await h.context.testUI.openLibrary(); await h.settle();
    h.outage(); await h.context.testUI.reload();
    assert.equal(h.document.querySelector('img,a'), null);
    assert.equal(h.document.body.textContent.includes('Private Film'), false);
    assert.equal(h.document.querySelector('[data-page="admin"]'), null);
    assert.ok([...h.document.querySelectorAll('button')].some(button => button.textContent === 'Retry verification'));
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

test('appearance is usable before linking and persists only a cosmetic preference', async () => {
  const h = await harness(true, inbox, false);
  try {
    const control = h.document.querySelector('[aria-label="Switch color appearance"]'); control.click(); await h.settle();
    assert.equal(h.document.documentElement.dataset.appearance, 'light');
    assert.deepEqual(h.stored, [{ key: 'media-appearance', value: 'light' }]);
    assert.equal(h.document.querySelector('.account-chip,.media-card,.skeleton-card'), null);
    assert.doesNotMatch(h.document.body.textContent, /Private Viewer|Secret Server|Private Film/);
    assert.equal(h.calls.some(call => call.operation === 'browse'), false);
  } finally { h.close(); }
});

test('slow gallery loads provide generic accessible skeletons without artwork', async () => {
  const h = await harness();
  let release;
  try {
    h.control.delayBrowse = new Promise(resolve => { release = resolve; });
    const pending = h.context.testUI.openLibrary(); await h.settle();
    assert.equal(h.document.querySelector('.content').getAttribute('aria-busy'), 'true');
    assert.equal(h.document.querySelectorAll('.skeleton-card').length, 6);
    assert.equal(h.document.querySelector('.loading-state img'), null);
    assert.doesNotMatch(h.document.querySelector('.content').textContent, /Private Film/);
    release(); await pending;
    assert.equal(h.document.querySelector('.content').hasAttribute('aria-busy'), false);
    assert.ok(h.document.querySelector('.media-card'));
    assert.equal(h.document.querySelector('[data-page="library"]').getAttribute('aria-current'), 'page');
  } finally { release?.(); h.close(); }
});

test('empty collection filters give an actionable path to discovery', async () => {
  const h = await harness();
  try {
    await h.context.testUI.openLibrary();
    const filter = h.document.querySelector('[aria-label="Filter collection"]'); filter.value = 'No such title'; filter.dispatchEvent(new h.window.Event('input'));
    assert.ok(h.document.querySelector('.empty h2'));
    click(h.document, 'Explore Discover'); await h.settle();
    assert.equal(h.calls.filter(call => call.operation === 'browse').at(-1).page, 'search');
  } finally { h.close(); }
});

test('following gives an inline receipt without repeating the write or losing the gallery', async () => {
  const h = await harness();
  try {
    await h.context.testUI.openLibrary();
    click(h.document, '☆ Follow'); await h.settle();
    const followed = [...h.document.querySelectorAll('button')].find(button => button.textContent === '✓ Following');
    assert.equal(followed.getAttribute('aria-pressed'), 'true'); assert.equal(followed.disabled, true);
    followed.click(); await h.settle();
    assert.equal(h.calls.filter(call => call.operation === 'watch').length, 1);
    assert.ok(h.document.querySelector('.media-card'));
  } finally { h.close(); }
});

test('confirmation stays visible and non-dismissable while a mutation is pending', async () => {
  const h = await harness();
  let release;
  try {
    h.document.querySelector('[data-page="search"]').click(); await h.settle();
    click(h.document, '＋ Request'); await h.settle();
    const dialog = h.document.querySelector('dialog');
    assert.ok(dialog.getAttribute('aria-labelledby'));
    assert.equal(h.document.activeElement.textContent, 'Cancel');
    h.control.delayWrite = new Promise(resolve => { release = resolve; });
    click(h.document, 'Confirm'); await h.settle();
    assert.equal(dialog.isConnected, true); assert.equal(dialog.getAttribute('aria-busy'), 'true');
    dialog.dispatchEvent(new h.window.Event('cancel', { cancelable: true }));
    assert.equal(dialog.isConnected, true);
    click(h.document, 'Confirm'); await h.settle();
    assert.equal(h.calls.filter(call => call.operation === 'request').length, 1);
    release(); await h.settle();
    assert.equal(h.document.querySelector('dialog'), null);
    assert.match(h.document.querySelector('#notice').textContent, /Request recorded/);
  } finally { release?.(); h.close(); }
});

test('failed confirmations explain uncertainty and cannot silently replay a write', async () => {
  const h = await harness();
  try {
    h.document.querySelector('[data-page="search"]').click(); await h.settle();
    click(h.document, '＋ Request'); await h.settle(); h.control.failWrite = true;
    click(h.document, 'Confirm'); await h.settle();
    const dialog = h.document.querySelector('dialog');
    assert.match(dialog.querySelector('[role="alert"]').textContent, /Check the current state/);
    const submit = [...dialog.querySelectorAll('button')].find(button => button.textContent === 'Confirm');
    assert.equal(submit.hidden, true); assert.equal(submit.disabled, true);
    submit.click(); await h.settle(); assert.equal(h.calls.filter(call => call.operation === 'request').length, 1);
    click(h.document, 'Cancel'); await h.settle(); assert.equal(h.document.querySelector('dialog'), null);
  } finally { h.close(); }
});
