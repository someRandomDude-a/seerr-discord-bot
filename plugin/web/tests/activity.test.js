import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import vm from 'node:vm';
import { JSDOM } from 'jsdom';

async function harness(configure, appPath = '/base/MediaHub/Activity/App') {
  const dom = new JSDOM('<html><body><main id="app"></main></body></html>', { pretendToBeVisual: true, url: 'https://123456.discordsays.com' + appPath });
  const html = await readFile(new URL('../../src/Jellyfin.Plugin.MediaHub/Web/hub.html', import.meta.url), 'utf8');
  const source = (await readFile(new URL('../../activity/src/main.js', import.meta.url), 'utf8')).replace(/^import[^\n]+\n/, '');
  const control = { linked: true, user: '42' }, calls = [], subscriptions = {}, events = [];
  configure?.(control);
  const sdk = { guildId: '555', ready: async () => {}, subscribe: async (event, callback) => { subscriptions[event] = callback; }, commands: {
    authorize: async options => { events.push({ authorize: options }); return { code: 'discord-code' }; },
    authenticate: async options => { events.push({ authenticate: options }); return { user: { id: control.user } }; },
    openExternalLink: async options => { events.push({ open: options.url }); }
  } };
  const fetch = async (url, options = {}) => {
    calls.push({ url, options });
    if (control.reply) { const result = await control.reply(url, options); if (result) return result; }
    const suffix = appPath === '/' ? new URL(url).pathname.slice(1) : url.split('/MediaHub/Activity/')[1];
    const body = suffix === 'Config' ? { ApplicationId: '123456' } : suffix === 'Challenge' ? { Challenge: 'a'.repeat(48) } : suffix === 'Authenticate' ? { AccessToken: 'discord-access-only', Session: 'session-memory-only', Discord: '42' } : suffix === 'Profile' ? { Linked: control.linked, UserId: 'native-user', JellyfinUrl: 'https://jellyfin.example/base', Theme: { Background: '#112233', Text: 'url(private)' } } : suffix === 'Link' ? { Url: 'https://jellyfin.example/base/MediaHub/Link?handoff=' + 'b'.repeat(48) } : {};
    return { ok: true, status: 200, json: async () => body, blob: async () => new dom.window.Blob(['file']), text: async () => html };
  };
  dom.window.JellyfinMediaHub = view => { events.push('mounted'); view.addEventListener('viewhide', () => events.push('hidden')); };
  const append = dom.window.document.head.append.bind(dom.window.document.head);
  dom.window.document.head.append = element => { append(element); queueMicrotask(() => element.onload()); };
  vm.runInContext(source, vm.createContext({ window: dom.window, document: dom.window.document, URL, DOMParser: dom.window.DOMParser, Event: dom.window.Event, FormData: dom.window.FormData,
    DiscordSDK: function () { return sdk; }, fetch }));
  const settle = () => new Promise(resolve => setImmediate(resolve));
  await settle(); await settle();
  return { dom, calls, control, events, subscriptions, settle, close: () => { dom.window.dispatchEvent(new dom.window.Event('pagehide')); dom.window.close(); } };
}

test('Activity uses bundled shared controls, an in-memory session and base-path-aware same-origin APIs only', async () => {
  const h = await harness();
  try {
    assert.ok(h.events.includes('mounted')); assert.ok(h.calls.every(c => c.url.startsWith('https://123456.discordsays.com/base/MediaHub/Activity/')));
    assert.equal(h.dom.window.localStorage.length, 0); assert.equal(h.dom.window.sessionStorage.length, 0);
    assert.equal(h.dom.window.document.querySelector('[data-link]').closest('section').hidden, true);
    const content = h.dom.window.document.querySelector('#hubMessageText');
    assert.equal(h.dom.window.document.querySelector('label[for="hubMessageText"]').textContent, content.getAttribute('label'));
    assert.equal(h.dom.window.document.documentElement.style.getPropertyValue('--hub-background'), '#112233');
    assert.equal(h.dom.window.document.documentElement.style.getPropertyValue('--hub-text'), '');
    await h.dom.window.ApiClient.ajax({ url: h.dom.window.ApiClient.getUrl('MediaHub/Me'), data: '{"Revision":0}' });
    const call = h.calls.at(-1); assert.equal(call.options.headers['X-MediaHub-Session'], 'session-memory-only'); assert.equal(call.options.credentials, 'omit'); assert.equal(call.options.redirect, 'error');
    assert.equal(call.options.headers.Authorization, undefined); assert.doesNotMatch(h.dom.window.document.body.textContent, /discord-access-only|session-memory-only/);
    await h.dom.window.MediaHubOpenNative('https://jellyfin.example/base/web/index.html#!/details?id=item');
    assert.match(h.events.at(-1).open, /^https:\/\/jellyfin.example\/base\/web/);
  } finally { h.close(); }
});

test('unlinked Activity opens an HTTPS native link explicitly and never reads private media', async () => {
  const h = await harness(control => { control.linked = false; });
  try {
    assert.equal(h.events.includes('mounted'), false); assert.equal(h.calls.some(c => c.url.includes('/Api/')), false);
    assert.equal(h.calls.some(c => c.url.endsWith('/Link')), false);
    h.dom.window.document.querySelector('button').click(); await h.settle(); assert.match(h.events.at(-1).open, /^https:\/\/jellyfin.example\/base\/MediaHub\/Link\?handoff=/);
  } finally { h.close(); }
});
test('Activity party handoff explicitly opens native controls without publishing a bearer or invoking player routes', async () => {
  const h = await harness();
  try {
    const url = 'https://jellyfin.example/base/web/index.html#!/userpluginsettings.html?pageUrl=%2Fweb%2FConfigurationPage%3Fname%3Dmediahub';
    h.control.reply = async target => target.endsWith('/Api/NativeHub') ? { ok: true, status: 200, json: async () => ({ Url: url }) } : undefined;
    assert.equal(h.calls.some(c => c.url.endsWith('/Api/NativeHub')), false);
    h.dom.window.document.querySelector('[data-native-party-handoff] button').click(); await h.settle();
    assert.equal(h.events.at(-1).open, url); assert.equal(h.calls.some(c => /\/Players|\/WatchParty/.test(c.url)), false);
    assert.doesNotMatch(h.dom.window.document.body.textContent, /session-memory-only|discord-access-only/);
  } finally { h.close(); }
});

test('Discord directory-mapped root never duplicates the native server base path', async () => {
  const h = await harness(undefined, '/');
  try {
    assert.ok(h.events.includes('mounted'));
    assert.equal(h.calls[0].url, 'https://123456.discordsays.com/Config');
    assert.equal(h.dom.window.ApiClient.getUrl('MediaHub/Me'), 'https://123456.discordsays.com/Api/Me');
    assert.equal(h.dom.window.document.querySelector('script').src, 'https://123456.discordsays.com/Assets/mediahub.messenger.js');
  } finally { h.close(); }
});

test('Discord account changes clear the mounted page and revoke only the disposable Activity session', async () => {
  const h = await harness();
  try {
    h.subscriptions.CURRENT_USER_UPDATE({ id: '99' }); await h.settle();
    assert.ok(h.events.includes('hidden')); assert.match(h.dom.window.document.body.textContent, /account changed/);
    assert.equal(h.calls.filter(c => c.url.endsWith('/Logout')).length, 1);
    assert.equal(h.dom.window.MediaHubNativeUrl, undefined);
    await assert.rejects(h.dom.window.ApiClient.ajax({ url: h.dom.window.ApiClient.getUrl('MediaHub/Me') }), /Reopen Activity/);
  } finally { h.close(); }
});

test('SDK identity mismatch never mounts a view and late private reads cannot survive an expiry', async () => {
  const mismatch = await harness(control => { control.user = '99'; });
  try { assert.equal(mismatch.events.includes('mounted'), false); assert.equal(mismatch.calls.some(c => c.url.endsWith('/Profile')), false); assert.match(mismatch.dom.window.document.body.textContent, /identity changed/); }
  finally { mismatch.close(); }
  const h = await harness();
  try {
    h.control.reply = async url => url.includes('/Api/') ? { ok: false, status: 401, json: async () => ({ Error: 'Expired' }) } : undefined;
    await assert.rejects(h.dom.window.ApiClient.ajax({ url: h.dom.window.ApiClient.getUrl('MediaHub/Me') }), /Expired/);
    assert.ok(h.events.includes('hidden')); assert.equal(h.dom.window.document.querySelector('#mediaHubPage'), null);
  } finally { h.close(); }
});
