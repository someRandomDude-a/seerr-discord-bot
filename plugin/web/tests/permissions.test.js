import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import vm from 'node:vm';
import { JSDOM } from 'jsdom';

async function harness() {
  const html = await readFile(new URL('../../src/Jellyfin.Plugin.MediaHub/Web/hub.html', import.meta.url), 'utf8');
  const js = await readFile(new URL('../../src/Jellyfin.Plugin.MediaHub/Web/permissions.js', import.meta.url), 'utf8');
  const dom = new JSDOM(html, { url: 'https://jellyfin.example/web/' }); dom.window.confirm = () => true;
  const view = dom.window.document.querySelector('#mediaHubPage'), calls = [], data = { Revision: 7, Permissions: { Default: 1, Users: [] }, Accounts: [{ Id: 'user', Name: '<img src=x> Native user' }] };
  let fail = false;
  const api = async (path, body) => { calls.push({ path, body }); if (fail && body) throw new Error('Uncertain'); return data; };
  vm.runInContext(js, vm.createContext({ window: dom.window, document: dom.window.document }));
  const ui = dom.window.JellyfinDeletionPermissions(view, api, () => {}), settle = () => new Promise(resolve => setImmediate(resolve));
  ui.show(); await settle();
  return { dom, view, ui, calls, settle, fail() { fail = true; }, close() { dom.window.close(); } };
}
test('permission drafts require explicit confirmation and use the exact administrator settings revision', async () => {
  const h = await harness();
  try {
    const form = h.view.querySelector('[data-deletion-permission-form]'); form.elements.scope.value = '2';
    form.querySelector('[data-deletion-set-override]').click(); assert.equal(h.calls.filter(c => c.body).length, 0);
    assert.match(h.view.querySelector('[data-deletion-overrides]').textContent, /<img src=x>/); assert.equal(h.view.querySelector('[data-deletion-overrides] img'), null);
    h.dom.window.confirm = () => false; form.dispatchEvent(new h.dom.window.Event('submit', { cancelable: true })); await h.settle(); assert.equal(h.calls.filter(c => c.body).length, 0);
    h.dom.window.confirm = () => true; form.dispatchEvent(new h.dom.window.Event('submit', { cancelable: true })); await h.settle();
    assert.deepEqual(JSON.parse(JSON.stringify(h.calls.at(-1).body)), { Revision: 7, Permissions: { Default: 1, Users: [{ User: 'user', Scope: 2 }] } });
  } finally { h.close(); }
});
test('uncertain permission writes cannot repeat and teardown removes the native account directory', async () => {
  const h = await harness();
  try {
    h.fail(); const form = h.view.querySelector('[data-deletion-permission-form]');
    form.dispatchEvent(new h.dom.window.Event('submit', { cancelable: true })); await h.settle();
    form.dispatchEvent(new h.dom.window.Event('submit', { cancelable: true })); await h.settle(); assert.equal(h.calls.filter(c => c.body).length, 1);
    h.view.dispatchEvent(new h.dom.window.Event('mediahubsettingshidden')); assert.equal(h.view.querySelector('[data-deletion-permissions]').hidden, true); assert.equal(form.elements.account.options.length, 0);
  } finally { h.close(); }
});
