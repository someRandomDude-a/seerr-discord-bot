import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import vm from 'node:vm';
import { JSDOM } from 'jsdom';

const guild = '1234567890123456789', channel = '1234567890123456790', user = '1234567890123456791';
async function harness({ privateHTTP = false, expireUploads = false, signedIn = true } = {}) {
  const html = await readFile(new URL('../../media_bot/panel_assets/index.html', import.meta.url), 'utf8');
  const source = (await Promise.all(['chat.js', 'messenger.js', 'panel.js'].map(name => readFile(new URL('../../media_bot/panel_assets/' + name, import.meta.url), 'utf8')))).join('\n')
    .replace(/^import .*;\r?\n/gm, '').replace(/export (async )?function/g, '$1function');
  const example = await readFile(new URL('../../.env.advanced.example', import.meta.url), 'utf8');
  const secretKeys = ['DISCORD_TOKEN', 'SEERR_ADMIN_KEY', 'DISCORD_CLIENT_SECRET', 'WEBHOOK_SECRET', 'LIDARR_API_KEY', 'READARR_API_KEY', 'RADARR_API_KEY', 'SONARR_API_KEY'];
  const fields = example.split(/\r?\n/).filter(line => line.includes('=') && !line.startsWith('#') && !line.startsWith('DATA_DIR='))
    .map(line => { const [key, value] = line.split(/=(.*)/s); return { key, default: value === 'replace-me' ? '' : value, help: 'Description', secret: secretKeys.includes(key) }; });
  let saved = { fields, values: Object.fromEntries(fields.map(f => [f.key, f.secret ? false : f.default])),
    locked: ['SEERR_URL'], verified: [], running: true, webhook_tests: {} };
  saved.values.DISCORD_TOKEN = true;
  const dom = new JSDOM(html);
  dom.window.HTMLDialogElement.prototype.showModal = function () { this.open = true; };
  dom.window.HTMLDialogElement.prototype.close = function () { this.open = false; };
  const calls = [];
  const events = [];
  const control = { authenticated: signedIn, networkFailure: false, serverFailure: false };
  let nonce = 0;
  const context = vm.createContext({ document: dom.window.document, FormData: dom.window.FormData, console,
    URL, crypto: privateHTTP ? { getRandomValues: values => { values.fill(++nonce); return values; } } : { randomUUID: () => `nonce-${String(++nonce).padStart(20, '0')}` },
    localStorage: { getItem: () => null, setItem: () => {} },
    EventSource: class {
      readyState = 1; handlers = new Map(); closed = false;
      constructor(path) { this.path = path; events.push(this); }
      addEventListener(name, fn) { this.handlers.set(name, fn); }
      emit(name) { this.handlers.get(name)?.({}); }
      close() { this.closed = true; }
    },
    fetch: async (path, options = {}) => {
      const args = options.body instanceof dom.window.FormData ? { file: options.body.get('file').name } : options.body ? JSON.parse(options.body) : {};
      calls.push({ path, ...args });
      if (control.networkFailure && path === '/api/state') throw new Error('Network unavailable');
      if (control.serverFailure && path === '/api/state') return { ok: false, status: 503, json: async () => ({ error: 'Temporarily unavailable' }) };
      if (path === '/api/login') {
        control.authenticated = args.code === 'console-code';
        return { ok: control.authenticated, status: control.authenticated ? 200 : 401,
          json: async () => control.authenticated ? { ok: true } : { error: 'Invalid or already used access code' } };
      }
      if (!control.authenticated) return { ok: false, status: 401, json: async () => ({ error: 'Panel sign-in required' }) };
      let body = saved;
      if (path === '/api/admin/uploads') {
        body = expireUploads ? { error: 'Session expired' } : { result: { id: 'uploaded-photo', filename: args.file, size: 8, image: true } };
      } else if (path === '/api/action') {
        saved = structuredClone(saved);
        for (const [key, value] of Object.entries(args.changes || {})) saved.values[key] = secretKeys.includes(key) ? Boolean(value) : value;
        for (const key of args.clear || []) saved.values[key] = secretKeys.includes(key) ? false : '';
        const result = args.operation === 'lidarr' ? { choices: { qualityprofile: [{ id: 1, name: 'Standard' }],
          metadataprofile: [{ id: 2, name: 'All' }], rootfolder: [{ id: 3, path: '/music' }] } } : { message: 'Verified' };
        body = { ...saved, result };
      } else if (path === '/api/admin') {
         const result = args.operation === 'state' ? { guilds: [{ id: guild, name: 'Cinema', channel: 'general', default_channel_id: channel,
           channels: [{ id: channel, name: 'general', category: 'Community', sendable: true }, { id: '1234567890123456792', name: 'locked', sendable: false }] }],
           threads: [{ kind: 'dm', id: user, name: 'Viewer', count: 1 }, { kind: 'guild', id: guild, name: 'Cinema', count: 1 }], jobs: [], all_users_enabled: false }
           : args.operation === 'inbox' ? { messages: [{ message_id: '1234567890123456800', author_id: user, author_name: 'Viewer', guild_id: guild,
             guild_name: 'Cinema', channel_id: channel, channel_name: 'general', content: '<img src=x onerror=alert(1)>', created_at: 1 }], total: 1 }
           : args.operation === 'history' ? { jobs: [{ id: 1, content: '**Announcement**', created_at: 1, deliveries: [{ label: 'Cinema / #general', status: 'sent' }] }], total: 1 }
          : args.operation === 'prepare' ? { plan: 'preview', channels: 1, users: 0, destinations: ['Cinema / #general'], skipped: [], message: args.message }
          : { job: 1 };
        body = { result };
      }
      return { ok: !(expireUploads && path === '/api/admin/uploads'), status: expireUploads && path === '/api/admin/uploads' ? 401 : 200, json: async () => body };
    } });
  vm.runInContext(source, context);
  const settle = async () => { for (let i = 0; i < 8; i++) await new Promise(resolve => setImmediate(resolve)); };
  await settle();
  const click = text => {
    const button = [...dom.window.document.querySelectorAll('button')].find(element => element.textContent === text);
    assert.ok(button, `Missing button: ${text}`); button.click();
  };
  return { document: dom.window.document, window: dom.window, context, calls, events, control, click, settle, close: () => dom.window.close() };
}

test('panel keeps saved secrets blank and environment overrides read-only', async () => {
  const h = await harness();
  try {
    const token = h.document.querySelector('[data-key="DISCORD_TOKEN"]');
    assert.equal(token.value, ''); assert.match(token.placeholder, /Saved/);
    assert.equal(token.type, 'password');
    assert.equal(h.document.querySelector('[data-key="SEERR_URL"]').disabled, true);
    h.click('Activity'); await h.settle();
    assert.equal(h.document.querySelector('[data-key="ACTIVITY_ENABLED"]').value, 'true');
    assert.equal(h.calls.some(call => call.operation === 'save'), false);
  } finally { h.close(); }
});

test('panel builds direct integration dropdowns from verification results', async () => {
  const h = await harness();
  try {
    await vm.runInContext('action("lidarr").then(() => render())', h.context);
    const quality = h.document.querySelector('[data-key="LIDARR_QUALITY_PROFILE_ID"]');
    assert.equal(quality.tagName, 'SELECT');
    assert.deepEqual([...quality.options].map(option => option.value), ['0', '1']);
    assert.match(h.document.querySelector('[data-key="LIDARR_ROOT_FOLDER"]').textContent, /\/music/);
  } finally { h.close(); }
});

test('panel groups and filters inbox safely without converting Discord IDs to numbers', async () => {
  const h = await harness();
  try {
    h.click('Messages'); await h.settle();
    assert.equal(h.document.querySelector('.received-content').textContent, '<img src=x onerror=alert(1)>');
    assert.equal(h.document.querySelector('.received-content img'), null);
    h.click('# general'); await h.settle();
    assert.equal(h.calls.filter(call => call.operation === 'inbox').at(-1).guild_id, guild);
    const form = h.document.querySelector('.chat-search');
    form.querySelector('input').value = '';
    form.dispatchEvent(new h.window.Event('submit', { cancelable: true })); await h.settle();
    assert.equal(h.calls.filter(call => call.operation === 'inbox').at(-1).query, '');
  } finally { h.close(); }
});

test('announcements target the selected channel and cannot send without explicit confirmation', async () => {
  const h = await harness();
  try {
    h.click('Messages'); await h.settle(); h.click('Reply'); await h.settle();
    const mode = h.document.querySelector('[aria-label="Message mode"]'); mode.value = 'announcement'; mode.dispatchEvent(new h.window.Event('change'));
    h.document.querySelector('.panel-draft').value = 'A reply';
    h.click('Preview announcement'); await h.settle();
    const preview = h.calls.find(call => call.operation === 'prepare');
    assert.deepEqual(preview.guild_ids, [guild]); assert.equal(preview.channel_id, channel);
    assert.equal(h.calls.some(call => call.operation === 'send'), false);
    h.click('Confirm'); await h.settle();
    assert.equal(h.calls.find(call => call.operation === 'send').confirmed, true);
  } finally { h.close(); }
});

test('normal DM replies send directly to one user without an announcement preview', async () => {
  const h = await harness();
  try {
    h.click('Messages'); await h.settle(); h.click('Viewer'); await h.settle();
    assert.equal(h.calls.filter(call => call.operation === 'inbox').at(-1).user_id, user);
    h.document.querySelector('.panel-draft').value = 'A direct reply'; h.click('Send'); await h.settle();
    const send = h.calls.find(call => call.operation === 'chat');
    assert.equal(send.user_id, user); assert.equal(send.guild_id, null); assert.equal(send.channel_id, null);
    assert.ok(send.request_id); assert.equal(h.calls.some(call => call.operation === 'prepare'), false);
  } finally { h.close(); }
});

test('live updates preserve the focused draft and subscriptions close when leaving Messages', async () => {
  const h = await harness();
  try {
    h.click('Messages'); await h.settle(); h.click('Viewer'); await h.settle();
    const draft = h.document.querySelector('.panel-draft'); draft.value = 'Do not erase this'; draft.dispatchEvent(new h.window.Event('input')); draft.focus();
    const before = h.calls.filter(call => call.operation === 'inbox').length;
    h.events[0].emit('update'); await h.settle();
    assert.ok(h.calls.filter(call => call.operation === 'inbox').length > before);
    assert.equal(h.document.querySelector('.panel-draft'), draft); assert.equal(draft.value, 'Do not erase this');
    assert.equal(h.document.activeElement, draft);
    h.click('Setup'); await h.settle(); assert.equal(h.events[0].closed, true);
  } finally { h.close(); }
});

test('announcement history is a channel-like feed with recipient delivery states and theme controls', async () => {
  const h = await harness();
  try {
    h.click('Messages'); await h.settle(); h.click('# announcements'); await h.settle();
    assert.equal(h.document.querySelector('.received-content strong').textContent, 'Announcement');
    assert.ok(h.document.querySelector('.chat-delivery-details').textContent.includes('Cinema / #general'));
    const theme = h.document.querySelector('[aria-label="Chat theme"]'); theme.value = 'amethyst'; theme.dispatchEvent(new h.window.Event('change'));
    assert.equal(h.document.querySelector('.chat-shell').dataset.theme, 'amethyst');
    assert.equal(h.document.querySelectorAll('.chat-thread:disabled').length, 1);
  } finally { h.close(); }
});

test('normal/announcement mode changes keep the conversation draft and work on private HTTP', async () => {
  const h = await harness({ privateHTTP: true });
  try {
    h.click('Messages'); await h.settle(); h.click('Viewer'); await h.settle();
    const draft = h.document.querySelector('.panel-draft'); draft.value = 'Keep this draft'; draft.dispatchEvent(new h.window.Event('input'));
    let mode = h.document.querySelector('[aria-label="Message mode"]'); mode.value = 'announcement'; mode.dispatchEvent(new h.window.Event('change'));
    assert.equal(h.document.querySelector('.panel-draft').value, 'Keep this draft');
    mode = h.document.querySelector('[aria-label="Message mode"]'); mode.value = 'normal'; mode.dispatchEvent(new h.window.Event('change'));
    h.click('Send'); await h.settle();
    assert.match(h.calls.find(call => call.operation === 'chat').request_id, /^[0-9a-f]{48}$/);
  } finally { h.close(); }
});

test('file uploads preview privately and are attached to the inline DM send', async () => {
  const h = await harness();
  try {
    h.click('Messages'); await h.settle(); h.click('Viewer'); await h.settle();
    const input = h.document.querySelector('input[type="file"]');
    Object.defineProperty(input, 'files', { value: [new h.window.File(['image'], 'photo.png')] });
    input.dispatchEvent(new h.window.Event('change')); await h.settle();
    assert.equal(h.document.querySelector('.chat-upload-list img').getAttribute('src'), '/api/admin/files/uploaded-photo');
    assert.equal(h.calls.some(call => call.operation === 'chat'), false);
    h.click('Send'); await h.settle();
    assert.deepEqual(h.calls.find(call => call.operation === 'chat').uploads, ['uploaded-photo']);
    assert.equal(h.document.querySelector('.chat-upload-list img'), null);
  } finally { h.close(); }
});

test('an expired upload session removes the private messenger and closes its live subscription', async () => {
  const h = await harness({ expireUploads: true });
  try {
    h.click('Messages'); await h.settle(); h.click('Viewer'); await h.settle();
    const input = h.document.querySelector('input[type="file"]');
    Object.defineProperty(input, 'files', { value: [new h.window.File(['image'], 'photo.png')] });
    input.dispatchEvent(new h.window.Event('change')); await h.settle();
    assert.equal(h.document.querySelector('.chat-shell'), null);
    assert.equal(h.events[0].closed, true);
    assert.match(h.document.body.textContent, /Panel session expired/);
    assert.ok(h.document.querySelector('#login'));
  } finally { h.close(); }
});

test('opening the panel without a cookie keeps a usable login form rather than claiming expiry', async () => {
  const h = await harness({ signedIn: false });
  try {
    assert.ok(h.document.querySelector('#login'));
    assert.equal(h.document.querySelector('#login-error').hidden, true);
    assert.doesNotMatch(h.document.querySelector('#login-error').textContent, /expired/i);
    h.document.querySelector('#login input').value = 'console-code';
    h.document.querySelector('#login').dispatchEvent(new h.window.Event('submit', { cancelable: true })); await h.settle();
    assert.ok(h.document.querySelector('[data-key="DISCORD_TOKEN"]'));
    assert.equal(h.document.querySelector('#login'), null);
  } finally { h.close(); }
});

test('a wrong or consumed code shows a login error and allows a successful retry', async () => {
  const h = await harness({ signedIn: false });
  try {
    const form = h.document.querySelector('#login');
    form.querySelector('input').value = 'wrong';
    form.dispatchEvent(new h.window.Event('submit', { cancelable: true })); await h.settle();
    assert.equal(h.document.querySelector('#login'), form);
    assert.match(h.document.querySelector('#login-error').textContent, /Invalid or already used/);
    assert.doesNotMatch(h.document.querySelector('#login-error').textContent, /session expired/i);
    form.querySelector('input').value = 'console-code';
    form.dispatchEvent(new h.window.Event('submit', { cancelable: true })); await h.settle();
    assert.ok(h.document.querySelector('[data-key="DISCORD_TOKEN"]'));
  } finally { h.close(); }
});

test('an expired live session removes private content and permits reauthentication in the same page', async () => {
  const h = await harness();
  try {
    h.click('Messages'); await h.settle(); h.click('Viewer'); await h.settle();
    h.control.authenticated = false; h.events[0].emit('error'); await h.settle();
    assert.equal(h.document.querySelector('.chat-shell'), null);
    assert.equal(h.events[0].closed, true);
    const form = h.document.querySelector('#login'); assert.ok(form);
    assert.match(h.document.querySelector('#login-error').textContent, /Panel session expired/);
    form.querySelector('input').value = 'console-code';
    form.dispatchEvent(new h.window.Event('submit', { cancelable: true })); await h.settle();
    h.click('Messages'); await h.settle();
    assert.ok(h.document.querySelector('.chat-shell'));
    assert.equal(h.events.at(-1).closed, false);
  } finally { h.close(); }
});

test('tunnel and server interruptions do not masquerade as expiry or permanently close live updates', async () => {
  const h = await harness();
  try {
    h.click('Messages'); await h.settle(); h.click('Viewer'); await h.settle();
    const draft = h.document.querySelector('.panel-draft'); draft.value = 'Keep my reply'; draft.dispatchEvent(new h.window.Event('input'));
    for (const fault of ['networkFailure', 'serverFailure']) {
      h.control[fault] = true; h.events[0].emit('error'); await h.settle();
      assert.equal(h.document.querySelector('.panel-draft'), draft);
      assert.equal(h.events[0].closed, false);
      assert.equal(h.document.querySelector('#login'), null);
      assert.doesNotMatch(h.document.querySelector('#notice').textContent, /expired/i);
      h.control[fault] = false;
    }
    h.events[0].emit('open'); h.events[0].emit('update'); await h.settle();
    assert.equal(draft.value, 'Keep my reply');
    assert.equal(h.document.querySelector('.chat-live').textContent, '● Live');
  } finally { h.close(); }
});

test('a late unauthorized response from the old session cannot expire a new sign-in', async () => {
  const h = await harness();
  try {
    const fetch = h.context.fetch;
    let release;
    h.context.fetch = async () => new Promise(resolve => { release = resolve; });
    const pending = vm.runInContext('api("/api/state").catch(error => error.message)', h.context);
    h.context.fetch = fetch;
    h.control.authenticated = false;
    await vm.runInContext('api("/api/state").catch(() => {})', h.context);
    const form = h.document.querySelector('#login');
    form.querySelector('input').value = 'console-code';
    form.dispatchEvent(new h.window.Event('submit', { cancelable: true })); await h.settle();
    assert.equal(h.document.querySelector('#login'), null);
    release({ ok: false, status: 401, json: async () => ({ error: 'Old session expired' }) });
    assert.match(await pending, /Panel session changed/);
    assert.equal(h.document.querySelector('#login'), null);
    assert.ok(h.document.querySelector('[data-key="DISCORD_TOKEN"]'));
  } finally { h.close(); }
});
