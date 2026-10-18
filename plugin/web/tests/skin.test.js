import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import { JSDOM } from 'jsdom';

test('native template uses Jellyfin component labels, containers and checkbox structure', async () => {
  const html = await readFile(new URL('../../src/Jellyfin.Plugin.MediaHub/Web/hub.html', import.meta.url), 'utf8');
  const dom = new JSDOM(html); const page = dom.window.document.querySelector('#mediaHubPage');
  try {
    assert.ok(page.dataset.require.split(',').includes('emby-textarea'));
    assert.equal(page.querySelector('style,link[rel="stylesheet"],[style]'), null);
    for (const input of page.querySelectorAll('[is="emby-input"],[is="emby-select"],[is="emby-textarea"]')) {
      assert.ok(input.getAttribute('label')?.trim(), input.id + ' needs the native label attribute');
      assert.ok(input.parentElement.classList.contains(input.tagName === 'SELECT' ? 'selectContainer' : 'inputContainer'));
      assert.equal(page.querySelector(`label[for="${input.id}"]`), null, 'Native component supplies its own label');
    }
    for (const input of page.querySelectorAll('input[type="checkbox"]')) {
      assert.equal(input.getAttribute('is'), 'emby-checkbox');
      assert.equal(input.parentElement.tagName, 'LABEL');
      assert.ok(input.parentElement.classList.contains('emby-checkbox-label'));
      assert.ok(input.parentElement.querySelector('span'));
      assert.ok(input.closest('.checkboxContainer'));
    }
    for (const button of page.querySelectorAll('button')) {
      assert.equal(button.getAttribute('is'), 'emby-button');
      assert.ok(button.classList.contains('emby-button'));
    }
  } finally { dom.window.close(); }
});
test('native dynamic components never use unsupported v1 creation options on Jellyfin v0', async () => {
  for (const name of ['settings', 'hub', 'watchparty', 'messenger', 'items']) {
    const source = await readFile(new URL(`../../src/Jellyfin.Plugin.MediaHub/Web/${name}.js`, import.meta.url), 'utf8');
    assert.doesNotMatch(source, /createElement\([^\n]*\{\s*is:/, name);
  }
});
