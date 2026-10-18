import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import vm from 'node:vm';
import { JSDOM } from 'jsdom';

const id = '11111111222233334444555555555555';
async function harness(details = false) {
  const dom = new JSDOM(`<html><body><header class="skinHeader"><div class="headerRight"></div></header><div class="card" data-id="${id}" data-type="Movie" data-serverid="server"></div>${details ? '<div class="itemDetailPage"><div class="detailButtons"><button data-native-play>Native play</button></div></div>' : ''}</body></html>`, { pretendToBeVisual: true, url: 'https://jellyfin.example/base/web/index.html' + (details ? '#/details?id=' + id + '&serverId=server' : '') });
  const calls = [], timers = []; const state = { user: 'viewer', revision: 0, subscribed: false, saved: false, queue: [], consent: [], feed: [], denied: false };
  dom.window.ApiClient = { getCurrentUserId: () => state.user, serverId: () => 'server', getUrl: path => '/base/' + path,
    ajax: async options => {
      const body = options.data ? JSON.parse(options.data) : null; calls.push({ path: options.url, body });
      if (state.denied) throw { status: 403 };
      if (options.url.endsWith('/Me/Consent')) return state.consent;
      if (options.url.includes('/Me/Consent/')) { state.consent = []; return { Saved: true }; }
      if (options.url.endsWith('/Me/Notifications')) return state.feed;
      if (options.url.endsWith('/Seen')) { state.feed.forEach(row => { row.Seen = true; }); return { Saved: true }; }
      if (options.url.endsWith('/DeletionRequests')) return [{ RequestId: 7, Is4k: false }];
      if (options.url.endsWith('/Deletions')) return body ? { Saved: true } : state.queue;
      if (options.url.endsWith('/Controls')) return { Id: id, Kind: 'Movie', Title: 'Movie' };
      if (body) { assert.equal(body.Revision, state.revision); state.revision++; state.saved = true; if ('Subscribed' in body) state.subscribed = body.Subscribed; }
      return { Revision: state.revision, Watchlist: state.saved ? [{ ItemId: id, Subscribed: state.subscribed }] : [] };
    } };
  const source = await readFile(new URL('../../src/Jellyfin.Plugin.MediaHub/Web/items.js', import.meta.url), 'utf8');
  vm.runInContext(source, vm.createContext({ window: dom.window, document: dom.window.document, MutationObserver: dom.window.MutationObserver,
    queueMicrotask, setInterval: fn => { timers.push(fn); return fn; }, clearInterval() {} }));
  const settle = () => new Promise(resolve => setImmediate(resolve)); await settle(); await settle();
  return { dom, calls, state, timers, settle, close: () => { dom.window.mediaHubItemControls.stop(); dom.window.close(); } };
}
test('card bell uses confirmed watchlist state without changing native card behavior', async () => {
  const h = await harness();
  try {
    const card = h.dom.window.document.querySelector('.card'); const controls = card.querySelector('[data-mediahub-controls]');
    assert.ok(controls); assert.equal(card.dataset.id, id); assert.equal(h.calls.some(call => call.path.endsWith('/Activity')), false);
    controls.querySelector('[data-bell]').click(); await h.settle();
    assert.equal(controls.querySelector('[data-bell]').getAttribute('aria-pressed'), 'true');
    controls.querySelector('[data-bell]').click(); await h.settle();
    assert.equal(h.calls.filter(call => call.body).length, 2);
    assert.equal(h.state.subscribed, false);
    assert.equal(h.dom.window.document.querySelector('style,link[rel=stylesheet]'), null);
  } finally { h.close(); }
});
test('card deletion requires an owned request choice and explicit confirmation', async () => {
  const h = await harness();
  try {
    const controls = h.dom.window.document.querySelector('[data-mediahub-controls]');
    [...controls.querySelectorAll('button')].find(button => button.textContent === 'Propose deletion').click(); await h.settle();
    assert.equal(h.calls.some(call => call.path.endsWith('/Deletions') && call.body), false);
    controls.querySelector('[data-request-choice]').click(); await h.settle();
    const write = h.calls.find(call => call.path.endsWith('/Deletions') && call.body); assert.equal(write.body.RequestId, 7); assert.equal(write.body.Item, id);
  } finally { h.close(); }
});
test('account changes and stopping remove all plugin-owned controls', async () => {
  const h = await harness();
  try {
    h.state.user = ''; await h.timers[0](); await h.settle();
    assert.equal(h.dom.window.document.querySelector('[data-mediahub-controls]'), null);
    assert.ok(h.dom.window.document.querySelector('.card'));
  } finally { h.close(); }
});
test('bubbling Jellyfin view pagehide does not stop automatic controls; real window teardown clears them', async () => {
  const h = await harness();
  try {
    const nativePage = h.dom.window.document.querySelector('.card'), bell = h.dom.window.document.querySelector('[data-notification-bell]');
    nativePage.dispatchEvent(new h.dom.window.Event('pagehide', { bubbles: true }));
    assert.equal(bell.isConnected, true); assert.ok(nativePage.querySelector('[data-mediahub-controls]'));
    h.dom.window.dispatchEvent(new h.dom.window.Event('pagehide'));
    assert.equal(h.dom.window.document.querySelector('[data-mediahub-navigation],[data-mediahub-controls]'), null);
    assert.equal(nativePage.isConnected, true);
  } finally { h.close(); }
});
test('bfcache pagehide clears private DOM and pageshow reprojects current access without duplicate controls', async () => {
  const h = await harness();
  try {
    h.dom.window.dispatchEvent(new h.dom.window.PageTransitionEvent('pagehide', { persisted: true }));
    assert.equal(h.dom.window.document.querySelector('[data-mediahub-navigation]'), null);
    h.dom.window.dispatchEvent(new h.dom.window.PageTransitionEvent('pageshow', { persisted: true })); await h.settle(); await h.settle();
    assert.equal(h.dom.window.document.querySelectorAll('[data-notification-bell]').length, 1);
    assert.equal(h.calls.some(row => row.body), false);
  } finally { h.close(); }
});
test('supported native detail pages receive verified item controls without replacing native buttons or recording passive activity', async () => {
  const h = await harness(true);
  try {
    const native = h.dom.window.document.querySelector('[data-native-play]');
    const controls = h.dom.window.document.querySelector('.detailButtons [data-mediahub-controls]'); assert.ok(controls);
    assert.equal(h.calls.filter(c => c.path.endsWith('/Controls')).length, 1); assert.equal(h.calls.some(c => c.path.endsWith('/Activity')), false);
    assert.equal(controls.parentElement.querySelector('[data-native-play]'), native);
    h.dom.window.location.hash = '#/home'; await h.settle(); await h.settle();
    assert.equal(h.dom.window.document.querySelector('.detailButtons [data-mediahub-controls]'), null); assert.equal(native.isConnected, true);
  } finally { h.close(); }
});
test('top navigation uses native bell, unread count and a base-path-aware watchlist page without implicit writes', async () => {
  const h = await harness();
  try {
    h.state.feed = [{ Id: 'update', Title: '<img src=x>', Status: 'Available', Seen: false }];
    await h.timers[0](); await h.settle();
    const nav = h.dom.window.document.querySelector('[data-mediahub-navigation]'), bell = nav.querySelector('[data-notification-bell]');
    assert.match(nav.querySelector('a').href, /#\/userpluginsettings\.html\?pageUrl=%2Fweb%2FConfigurationPage%3Fname%3Dmediahub&hubSection=watchlist$/);
    assert.equal(bell.querySelector('.material-icons').textContent, 'notifications_active'); assert.equal(bell.querySelector('.countIndicator').textContent, '1');
    bell.click(); await h.settle(); const menu = h.dom.window.document.querySelector('[role="dialog"]');
    assert.ok(menu); assert.equal(menu.classList.contains('dialog'), true, 'Native theme surface is required, not the browser white dialog default'); assert.equal(menu.style.color, 'inherit'); assert.match(menu.textContent, /<img src=x>/); assert.equal(menu.querySelector('img'), null);
    assert.equal(h.calls.filter(row => row.body).length, 0);
    [...menu.querySelectorAll('button')].find(node => node.textContent === 'Mark read').click(); await h.settle(); await h.settle();
    assert.equal(h.calls.filter(row => row.path.endsWith('/Seen')).length, 1);
    assert.equal(bell.querySelector('.countIndicator'), null);
  } finally { h.close(); }
});
test('removal markers describe proposals, never promise deletion, and consent requires explicit Yes or No', async () => {
  const h = await harness(true);
  try {
    h.state.queue = [{ Id: 'proposal', Item: id, State: 'queued', DueAt: '2026-10-20T12:00:00Z', AutomaticDeletion: false }];
    h.state.consent = [{ Id: 'notice', Proposal: 'proposal', Item: id, Title: 'A show' }];
    await h.timers[0](); await h.settle();
    const markers = h.dom.window.document.querySelectorAll('[data-removal-marker]'); assert.equal(markers.length, 2);
    for (const marker of markers) { assert.equal(marker.hidden, false); assert.match(marker.textContent, /Removal scheduled/); assert.match(marker.textContent, /automatic removal off/); }
    h.dom.window.document.querySelector('[data-notification-bell]').click(); await h.settle();
    const menu = h.dom.window.document.querySelector('[role="dialog"]'); assert.match(menu.textContent, /Allow deletion\?/);
    const help = menu.querySelector('[aria-label="Help about deletion"]'), details = help.parentElement.nextElementSibling;
    assert.equal(details.hidden, true); assert.equal(help.getAttribute('aria-expanded'), 'false');
    help.click(); assert.equal(details.hidden, false); assert.equal(help.getAttribute('aria-expanded'), 'true');
    assert.match(details.textContent, /Every current recent viewer must agree/); assert.match(details.textContent, /Automatic removal is off/);
    assert.match(details.textContent, /original requester/); assert.match(details.textContent, /2 days/);
    assert.equal([...menu.querySelectorAll('button')].find(node => node.textContent === 'Yes').classList.contains('button-submit'), true);
    help.click(); assert.equal(details.hidden, true);
    assert.equal(h.calls.some(row => row.body), false);
    [...menu.querySelectorAll('button')].find(node => node.textContent === 'No').click(); await h.settle(); await h.settle();
    assert.deepEqual(h.calls.find(row => row.path.endsWith('/Me/Consent/notice')).body, { Approve: false });
    assert.equal(h.calls.some(row => row.path.endsWith('/Activity')), false);
    h.state.queue = []; await h.timers[0](); await h.settle(); assert.ok([...markers].every(node => node.hidden));
  } finally { h.close(); }
});
test('Add to watchlist saves without forcing a notification subscription or replacing native UI', async () => {
  const h = await harness();
  try {
    h.dom.window.document.querySelector('[data-watch-add]').click(); await h.settle();
    const write = h.calls.find(row => row.path.endsWith('/Add')); assert.deepEqual(write.body, { Revision: 0 });
    assert.equal(h.state.saved, true); assert.equal(h.state.subscribed, false);
    assert.equal(h.dom.window.document.querySelector('[data-watch-add]').textContent, 'In watchlist');
  } finally { h.close(); }
});
test('access loss and account changes remove menu, markers, unread counts and all private top navigation', async () => {
  const h = await harness();
  try {
    h.dom.window.document.querySelector('[data-notification-bell]').click(); h.state.denied = true;
    await h.timers[0](); await h.settle();
    assert.equal(h.dom.window.document.querySelector('[data-mediahub-navigation],[role="dialog"],[data-mediahub-controls]'), null);
    assert.ok(h.dom.window.document.querySelector('.card'));
  } finally { h.close(); }
});
test('Jellyfin 12 visible native toolbar is preferred over the hidden legacy header and inherits host button classes', async () => {
  const h = await harness();
  try {
    h.dom.window.document.querySelector('.skinHeader').classList.add('noHeaderRight');
    const header = h.dom.window.document.createElement('header'); header.innerHTML = '<div class="MuiToolbar-root"><div data-native-right><button class="MuiIconButton-root host-theme-rule" aria-label="Native action"></button></div></div>';
    h.dom.window.document.body.append(header); await h.timers[0](); await h.settle();
    const bell = header.querySelector('[data-notification-bell]'); assert.ok(bell);
    assert.equal(bell.className, 'MuiIconButton-root host-theme-rule');
    assert.equal(h.dom.window.document.querySelectorAll('[data-notification-bell]').length, 1);
    assert.equal(bell.querySelector('.material-icons').textContent, 'notifications_none');
    assert.equal(h.dom.window.document.querySelector('.skinHeader [data-mediahub-navigation]'), null);
    assert.ok(header.querySelector('[aria-label="Native action"]'));
  } finally { h.close(); }
});
test('a replacement native toolbar with no buttons yet never leaves a hidden legacy bell or loses its glyph', async () => {
  const h = await harness();
  try {
    const header = h.dom.window.document.createElement('header'); header.innerHTML = '<div class="MuiToolbar-root"></div>'; h.dom.window.document.body.append(header);
    await h.timers[0](); await h.settle();
    assert.equal(h.dom.window.document.querySelectorAll('[data-notification-bell]').length, 1);
    const bell = header.querySelector('[data-notification-bell]'); assert.ok(bell); assert.equal(bell.classList.contains('headerButtonRight'), false);
    assert.equal(bell.querySelector('.material-icons').textContent, 'notifications_none');
    const native = h.dom.window.document.createElement('div'); native.innerHTML = '<button class="MuiIconButton-root replacement-theme"></button>'; header.querySelector('.MuiToolbar-root').prepend(native);
    await h.timers[0](); await h.settle();
    assert.equal(h.dom.window.document.querySelectorAll('[data-notification-bell]').length, 1);
    assert.equal(native.querySelector('[data-notification-bell]').className, 'MuiIconButton-root replacement-theme');
  } finally { h.close(); }
});
test('an open notification menu drops titles and consent controls when current visibility no longer projects them', async () => {
  const h = await harness();
  try {
    h.state.consent = [{ Id: 'notice', Title: 'Private movie' }]; await h.timers[0](); await h.settle();
    h.dom.window.document.querySelector('[data-notification-bell]').click(); await h.settle();
    assert.match(h.dom.window.document.querySelector('[role="dialog"]').textContent, /Private movie/);
    h.state.consent = []; await h.timers[0](); await h.settle();
    assert.doesNotMatch(h.dom.window.document.querySelector('[role="dialog"]').textContent, /Private movie/);
    assert.equal(h.calls.some(row => row.body), false);
  } finally { h.close(); }
});
test('opening the bell immediately after an account switch cannot reveal the previous account’s cached feed', async () => {
  const h = await harness();
  try {
    h.state.feed = [{ Id: 'old', Title: 'Previous account private title', Status: 'Available', Seen: false }]; await h.timers[0](); await h.settle();
    const bell = h.dom.window.document.querySelector('[data-notification-bell]'); h.state.user = 'another-viewer'; bell.click();
    assert.equal(h.dom.window.document.querySelector('[role="dialog"]'), null);
    assert.doesNotMatch(h.dom.window.document.body.textContent, /Previous account private title/);
    assert.equal(h.calls.some(row => row.body), false);
  } finally { h.close(); }
});
test('notification refresh retains the exact item’s focused action and expanded help, not the first matching Yes', async () => {
  const h = await harness();
  try {
    h.state.consent = [{ Id: 'one', Title: 'First movie' }, { Id: 'two', Title: 'Second movie' }]; await h.timers[0](); await h.settle();
    h.dom.window.document.querySelector('[data-notification-bell]').click(); await h.settle();
    const menu = h.dom.window.document.querySelector('[role="dialog"]');
    const help = menu.querySelector('[data-focus-key="help:two"]'); help.click(); help.focus();
    h.state.feed = [{ Id: 'update', Title: 'New update', Status: 'Available', Seen: false }]; await h.timers[0](); await h.settle();
    assert.equal(h.dom.window.document.activeElement.dataset.focusKey, 'help:two');
    const restored = menu.querySelector('[data-focus-key="help:two"]'); assert.equal(restored.getAttribute('aria-expanded'), 'true');
    assert.equal(h.dom.window.document.getElementById(restored.getAttribute('aria-controls')).hidden, false);
    const yes = menu.querySelector('[data-focus-key="consent:two:true"]'); yes.focus();
    h.state.feed[0].Seen = true; await h.timers[0](); await h.settle();
    assert.equal(h.dom.window.document.activeElement.dataset.focusKey, 'consent:two:true');
    assert.equal(h.calls.some(row => row.body), false);
  } finally { h.close(); }
});
test('notification keyboard navigation wraps both directions without focusing the background or submitting consent', async () => {
  const h = await harness();
  try {
    h.dom.window.document.querySelector('[data-notification-bell]').click(); await h.settle();
    const menu = h.dom.window.document.querySelector('[role="dialog"]'), close = [...menu.querySelectorAll('button')].find(node => node.textContent === 'Close'), all = menu.querySelector('a');
    all.focus(); all.dispatchEvent(new h.dom.window.KeyboardEvent('keydown', { key: 'Tab', bubbles: true, cancelable: true }));
    assert.equal(h.dom.window.document.activeElement, close);
    close.dispatchEvent(new h.dom.window.KeyboardEvent('keydown', { key: 'Tab', shiftKey: true, bubbles: true, cancelable: true }));
    assert.equal(h.dom.window.document.activeElement, all); assert.equal(h.calls.some(row => row.body), false);
  } finally { h.close(); }
});
