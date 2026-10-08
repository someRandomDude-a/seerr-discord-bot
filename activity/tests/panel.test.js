import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import vm from 'node:vm';
import { JSDOM } from 'jsdom';

const guild = '1234567890123456789', channel = '1234567890123456790', user = '1234567890123456791';
async function harness() {
  const html = await readFile(new URL('../../media_bot/panel_assets/index.html', import.meta.url), 'utf8');
  const source = await readFile(new URL('../../media_bot/panel_assets/panel.js', import.meta.url), 'utf8');
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
  const context = vm.createContext({ document: dom.window.document, FormData: dom.window.FormData, console,
    fetch: async (path, options = {}) => {
      const args = options.body ? JSON.parse(options.body) : {};
      calls.push({ path, ...args });
      let body = saved;
      if (path === '/api/action') {
        saved = structuredClone(saved);
        for (const [key, value] of Object.entries(args.changes || {})) saved.values[key] = secretKeys.includes(key) ? Boolean(value) : value;
        for (const key of args.clear || []) saved.values[key] = secretKeys.includes(key) ? false : '';
        const result = args.operation === 'lidarr' ? { choices: { qualityprofile: [{ id: 1, name: 'Standard' }],
          metadataprofile: [{ id: 2, name: 'All' }], rootfolder: [{ id: 3, path: '/music' }] } } : { message: 'Verified' };
        body = { ...saved, result };
      } else if (path === '/api/admin') {
        const result = args.operation === 'state' ? { guilds: [{ id: guild, name: 'Cinema', channel: 'general' }],
          threads: [{ kind: 'guild', id: guild, name: 'Cinema', count: 1 }], jobs: [], all_users_enabled: false }
          : args.operation === 'inbox' ? { messages: [{ author_id: user, author_name: 'Viewer', guild_id: guild,
            guild_name: 'Cinema', channel_id: channel, channel_name: 'general', content: '<img src=x onerror=alert(1)>', created_at: 1 }], total: 1 }
          : args.operation === 'prepare' ? { plan: 'preview', channels: 1, users: 0, destinations: ['Cinema / #general'], skipped: [], message: args.message }
          : { job: 1 };
        body = { result };
      }
      return { ok: true, json: async () => body };
    } });
  vm.runInContext(source, context);
  const settle = async () => { for (let i = 0; i < 8; i++) await new Promise(resolve => setImmediate(resolve)); };
  await settle();
  const click = text => {
    const button = [...dom.window.document.querySelectorAll('button')].find(element => element.textContent === text);
    assert.ok(button, `Missing button: ${text}`); button.click();
  };
  return { document: dom.window.document, window: dom.window, context, calls, click, settle, close: () => dom.window.close() };
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
    h.click('Cinema · 1'); await h.settle();
    assert.equal(h.calls.filter(call => call.operation === 'inbox').at(-1).guild_id, guild);
    const form = h.document.querySelector('.panel-inbox-tools');
    form.querySelector('[name="query"]').value = '';
    form.dispatchEvent(new h.window.Event('submit', { cancelable: true })); await h.settle();
    assert.equal(h.calls.filter(call => call.operation === 'inbox').at(-1).query, '');
  } finally { h.close(); }
});

test('panel reply targets original channel and cannot send without explicit confirmation', async () => {
  const h = await harness();
  try {
    h.click('Messages'); await h.settle(); h.click('Reply');
    h.document.querySelector('.panel-draft').value = 'A reply';
    h.click('Preview'); await h.settle();
    const preview = h.calls.find(call => call.operation === 'prepare');
    assert.deepEqual(preview.guild_ids, [guild]); assert.equal(preview.channel_id, channel);
    assert.equal(h.calls.some(call => call.operation === 'send'), false);
    h.click('Confirm'); await h.settle();
    assert.equal(h.calls.find(call => call.operation === 'send').confirmed, true);
  } finally { h.close(); }
});
