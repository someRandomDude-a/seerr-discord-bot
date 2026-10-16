import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import vm from 'node:vm';
import { JSDOM } from 'jsdom';

const id = '11111111222233334444555555555555';
async function harness() {
  const dom = new JSDOM(`<html><body><div class="card" data-id="${id}" data-type="Movie" data-serverid="server"></div></body></html>`, { pretendToBeVisual: true, url: 'https://jellyfin.example/base/web/index.html' });
  const calls = [], timers = []; const state = { user: 'viewer', revision: 0, subscribed: false };
  dom.window.ApiClient = { getCurrentUserId: () => state.user, serverId: () => 'server', getUrl: path => '/base/' + path,
    ajax: async options => {
      const body = options.data ? JSON.parse(options.data) : null; calls.push({ path: options.url, body });
      if (options.url.endsWith('/DeletionRequests')) return [{ RequestId: 7, Is4k: false }];
      if (options.url.endsWith('/Deletions')) return { Saved: true };
      if (body) { assert.equal(body.Revision, state.revision); state.revision++; state.subscribed = body.Subscribed; }
      return { Revision: state.revision, Watchlist: state.subscribed ? [{ ItemId: id, Subscribed: true }] : [] };
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
    assert.equal(h.calls.some(call => call.path.endsWith('/Deletions')), false);
    controls.querySelector('[data-request-choice]').click(); await h.settle();
    const write = h.calls.find(call => call.path.endsWith('/Deletions')); assert.equal(write.body.RequestId, 7); assert.equal(write.body.Item, id);
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
