import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import vm from 'node:vm';
import { JSDOM } from 'jsdom';

const initial = () => ({ Revision: 0, DiscordEnabled: false, PublicJellyfinUrl: '', AllowedGuilds: [], SeerrUrl: '', HasDiscordToken: false, HasSeerrKey: false, RecentActivityDays: 14, DeletionDelayDays: 2, AutomaticDeletion: false,
  Features: { Lidarr: { Url: '', Root: '', Quality: 0, Metadata: 0, Requests: false, DailyLimit: 10, HasKey: false }, Readarr: { Url: '', Root: '', Quality: 0, Metadata: 0, Requests: false, DailyLimit: 10, HasKey: false }, Operators: [], ArrDestinations: [], InboxChannels: [], ActivityEnabled: false, OAuthPkce: true, MessageRetentionDays: 7, MaxRecipients: 100 } });
async function harness(configure) {
  const html = await readFile(new URL('../../src/Jellyfin.Plugin.MediaHub/Web/hub.html', import.meta.url), 'utf8');
  const script = await readFile(new URL('../../src/Jellyfin.Plugin.MediaHub/Web/settings.js', import.meta.url), 'utf8');
  const dom = new JSDOM(html, { url: 'https://jellyfin.example/base/web/index.html' }), calls = [], control = { verified: true, saved: initial() };
  configure?.({ dom, control }); const view = dom.window.document.querySelector('#mediaHubPage'); let ui;
  const api = async (path, body) => {
    calls.push({ path, body }); if (control.reply) { const reply = await control.reply(path, body); if (reply !== undefined) return reply; }
    if (path !== 'Settings') throw new Error('Unexpected discovery route');
    if (body) control.saved = { ...control.saved, ...body, Revision: control.saved.Revision + 1, Features: { ...control.saved.Features, ...body.Features } };
    return structuredClone(control.saved);
  };
  vm.runInContext(script, vm.createContext({ window: dom.window, document: dom.window.document }));
  ui = dom.window.JellyfinMediaSettings(view, api, () => { if (!control.verified) { ui.hide(); throw new Error('Account changed.'); } }); ui.ready(structuredClone(control.saved));
  const form = view.querySelector('[data-configuration]'), selector = view.querySelector('[data-settings-section]');
  const change = (name, value) => { form.elements.namedItem(name).value = value; form.elements.namedItem(name).dispatchEvent(new dom.window.Event('input', { bubbles: true })); };
  const group = value => { selector.value = value; selector.dispatchEvent(new dom.window.Event('change')); };
  const click = text => [...view.querySelectorAll('button')].find(b => b.textContent === text && !b.closest('[data-settings-panel]')?.hidden)?.click();
  const settle = () => new Promise(resolve => setImmediate(resolve));
  return { dom, view, ui, form, control, calls, change, group, click, settle, submit() { form.dispatchEvent(new dom.window.Event('submit', { cancelable: true })); }, close() { dom.window.close(); } };
}

test('conventional first-run settings have native dropdowns/help, one save boundary and no progress storage', async () => {
  const h = await harness();
  try {
    assert.equal(h.view.querySelector('[data-settings]').hidden, false); assert.equal(h.view.querySelector('[data-tutorial-start],[data-onboarding]'), null);
    assert.equal(h.calls.length, 0); assert.equal(h.dom.window.localStorage.length, 0);
    for (const input of h.form.querySelectorAll('input,select')) {
      assert.ok(input.getAttribute('label')); assert.equal(input.parentElement.className, input.tagName === 'SELECT' ? 'selectContainer' : 'inputContainer');
      const help = h.view.querySelector('#' + input.getAttribute('aria-describedby')); assert.ok(help?.textContent); assert.equal(help.hidden, true);
    }
    const b = h.view.querySelector('[aria-label="Help for Public Jellyfin address"]'); b.click(); assert.equal(b.getAttribute('aria-expanded'), 'true');
    assert.equal(h.calls.length, 0); assert.equal(h.view.querySelector('style,[style],link[rel="stylesheet"]'), null);
  } finally { h.close(); }
});
test('dynamic fields support Jellyfin v0 upgrades with a parent and native labels, not v1 creation options', async () => {
  const h = await harness(({ dom }) => {
    const document = dom.window.document, create = document.createElement.bind(document);
    document.createElement = (tag, component) => {
      assert.notEqual(typeof component, 'object', 'Jellyfin v0 rejects v1 customized-built-in options');
      assert.ok(!component || !['emby-input', 'emby-select'].includes(component), 'Fields need their parent before upgrade');
      return create(tag);
    };
    const insert = dom.window.Element.prototype.insertAdjacentHTML;
    dom.window.Element.prototype.insertAdjacentHTML = function (position, markup) {
      insert.call(this, position, markup);
      for (const input of this.querySelectorAll('input[is="emby-input"],select[is="emby-select"]')) {
        assert.ok(input.parentNode); const label = create('label'); label.htmlFor = input.id; label.textContent = input.getAttribute('label'); input.before(label);
      }
    };
  });
  try {
    for (const input of h.form.querySelectorAll('[data-settings-fields] input,[data-settings-fields] select')) {
      assert.equal(input.previousElementSibling?.htmlFor, input.id); assert.equal(input.previousElementSibling?.textContent, input.getAttribute('label'));
    }
    h.group('destinations'); h.click('Add an approved server manually');
    for (const input of h.view.querySelectorAll('[data-approval-row] input,[data-approval-row] select')) assert.equal(input.previousElementSibling?.htmlFor, input.id);
    assert.equal(h.calls.length, 0);
  } finally { h.close(); }
});

test('section dropdown keeps original fields and drafts without saving or exposing secret values in review', async () => {
  const h = await harness();
  try {
    const input = h.form.elements.discordToken; h.group('discord'); h.change('discordToken', 'private-unsaved-token'); h.group('activity'); h.change('applicationId', '1234'); h.change('clientSecret', 'private-oauth'); h.group('discord');
    assert.equal(h.form.elements.discordToken, input); assert.equal(input.value, 'private-unsaved-token');
    assert.match(h.view.querySelector('[data-settings-changes]').textContent, /Discord bot token/);
    assert.doesNotMatch(h.view.querySelector('[data-settings-changes]').textContent, /private-unsaved-token|private-oauth|1234/);
    assert.equal(h.calls.length, 0); assert.equal(h.dom.window.localStorage.length, 0);
  } finally { h.close(); }
});

test('one explicit save includes settings from every section and blank passwords preserve saved secrets', async () => {
  const h = await harness();
  try {
    h.change('publicJellyfinUrl', 'https://media.example/base'); h.group('discord'); h.change('allowedServers', '555, 666'); h.change('discordToken', 'private-token');
    h.group('messaging'); h.change('retention', '14'); h.submit(); await h.settle();
    assert.equal(h.calls.length, 1); const body = h.calls[0].body;
    assert.equal(body.Revision, 0); assert.equal(body.DiscordEnabled, false); assert.equal(body.AutomaticDeletion, false);
    assert.deepEqual(Array.from(body.AllowedGuilds), ['555', '666']); assert.equal(body.Features.MessageRetentionDays, 14);
    assert.equal(body.SeerrKey, null); assert.equal(body.Features.DiscordClientSecret, null); assert.equal(body.Features.ActivityEnabled, false);
    assert.equal(h.form.elements.discordToken.value, ''); assert.match(h.view.querySelector('[data-settings-notice]').textContent, /saved together/);
  } finally { h.close(); }
});

test('failed and stale saves preserve drafts and are never automatically replayed', async () => {
  const h = await harness(({ control }) => { control.reply = async (_, body) => { if (body) throw new Error('Settings changed. Reload and review.'); }; });
  try {
    h.change('discordToken', 'preserve-this-draft'); h.submit(); await h.settle(); h.group('activity'); h.group('discord');
    assert.equal(h.form.elements.discordToken.value, 'preserve-this-draft'); assert.equal(h.calls.length, 1);
    assert.match(h.view.querySelector('[data-settings-notice]').textContent, /Settings changed/);
  } finally { h.close(); }
});

test('reload requires explicit draft-discard confirmation and does not save', async () => {
  const h = await harness();
  try {
    h.change('discordToken', 'draft'); h.dom.window.confirm = () => false; h.click('Reload saved settings'); await h.settle();
    assert.equal(h.calls.length, 0); assert.equal(h.form.elements.discordToken.value, 'draft');
    h.dom.window.confirm = () => true; h.click('Reload saved settings'); await h.settle();
    assert.equal(h.calls.length, 1); assert.equal(h.calls[0].body, undefined); assert.equal(h.form.elements.discordToken.value, '');
  } finally { h.close(); }
});

test('profile dropdown discovery reads saved credentials only and never auto-selects an option', async () => {
  const h = await harness(({ control }) => {
    control.saved.Revision = 2; control.saved.Features.Lidarr = { ...control.saved.Features.Lidarr, Url: 'http://lidarr', HasKey: true };
    control.reply = async path => path.startsWith('Configuration/Options/') ? { Roots: [{ Value: '/music', Label: '/music' }], QualityProfiles: [{ Value: '2', Label: '<img src=x> Lossless' }], MetadataProfiles: [{ Value: '3', Label: 'Albums' }] } : undefined;
  });
  try {
    h.group('music'); h.click('Load saved service folders and profiles'); await h.settle();
    assert.equal(h.calls[0].path, 'Configuration/Options/lidarr?revision=2'); assert.equal(h.calls[0].body, undefined);
    assert.equal(h.form.elements.lidarrQuality.value, ''); assert.equal(h.form.elements.lidarrRoot.value, '');
    assert.match(h.form.elements.lidarrQuality.textContent, /<img src=x>/); assert.equal(h.view.querySelector('img'), null);
    h.change('lidarrKey', 'uncommitted-secret'); h.click('Load saved service folders and profiles'); await h.settle(); assert.equal(h.calls.length, 1);
    assert.match(h.view.querySelector('[data-settings-notice]').textContent, /Save this service address/);
  } finally { h.close(); }
});

test('Seerr destination discovery is separate from explicit address approval and save', async () => {
  const h = await harness(({ control }) => { control.saved.SeerrUrl = 'https://seerr.example'; control.saved.HasSeerrKey = true; control.reply = async path => path.startsWith('Configuration/Destinations') ? [{ Service: 'radarr', Instance: 7, Url: 'https://arr.example/base', Approved: false }] : undefined; });
  try {
    h.group('destinations'); h.click('Find servers configured in saved Seerr'); await h.settle();
    assert.equal(h.view.querySelector('[data-settings-approvals]').childElementCount, 0);
    h.click('Add this independently verified address to the approval list'); assert.equal(h.calls.length, 1);
    const row = h.view.querySelector('[data-approval-row]'); assert.equal(row.querySelector('input[type="url"]').value, 'https://arr.example/base');
    h.submit(); await h.settle(); const body = h.calls.at(-1).body;
    assert.equal(body.Features.ArrDestinations[0].Instance, 7); assert.equal(body.Features.ArrDestinations[0].Service, 'radarr');
    assert.equal(h.view.querySelector('textarea[name="arrDestinations"]'), null);
  } finally { h.close(); }
});

test('account changes and access loss clear administrator drafts, paths and discovery results', async () => {
  const h = await harness();
  try {
    h.change('discordToken', 'private-draft'); h.control.verified = false;
    h.group('discord'); assert.equal(h.view.querySelector('[data-settings]').hidden, true); assert.equal(h.form.elements.discordToken.value, '');
    assert.equal(h.calls.length, 0);
  } finally { h.close(); }
});

test('late profile response cannot restore private configuration after teardown', async () => {
  let finish;
  const h = await harness(({ control }) => {
    control.saved.Features.Lidarr = { ...control.saved.Features.Lidarr, Url: 'http://lidarr', HasKey: true };
    control.reply = async () => new Promise(resolve => { finish = resolve; });
  });
  try {
    h.group('music'); h.click('Load saved service folders and profiles'); h.ui.hide();
    finish({ Roots: [{ Value: '/private', Label: '/private' }], QualityProfiles: [], MetadataProfiles: [] }); await h.settle();
    assert.equal(h.view.querySelector('[data-settings]').hidden, true); assert.doesNotMatch(h.form.textContent, /\/private/);
  } finally { h.close(); }
});

test('unsigned retained settings can be explicitly reviewed and saved without changing a field', async () => {
  const h = await harness(({ control }) => { control.saved.Revision = 7; control.saved.ConfigurationReviewRequired = true; });
  try {
    assert.equal(h.form.querySelector('[type="submit"]').disabled, false);
    h.submit(); await h.settle(); assert.equal(h.calls.length, 1); assert.equal(h.calls[0].body.Revision, 7);
    assert.equal(h.calls[0].body.AutomaticDeletion, false);
  } finally { h.close(); }
});

test('credential removal and service disconnect require explicit staging, confirmation and save', async () => {
  const h = await harness(({ control }) => { control.saved.Features.Lidarr = { Url: 'http://lidarr', Root: '/music', Quality: 2, Metadata: 3, Requests: true, DailyLimit: 10, HasKey: true }; });
  try {
    h.group('music'); h.click('Disconnect Lidarr on next Save'); assert.equal(h.calls.length, 0);
    assert.equal(h.form.elements.lidarrUrl.value, ''); assert.equal(h.form.elements.lidarrRequests.value, 'false'); assert.equal(h.form.elements.lidarrKeyAction.value, 'remove');
    h.dom.window.confirm = () => false; h.submit(); await h.settle(); assert.equal(h.calls.length, 0);
    h.dom.window.confirm = () => true; h.submit(); await h.settle(); assert.equal(h.calls.length, 1);
    assert.equal(h.calls[0].body.Features.Lidarr.Key, ''); assert.equal(h.calls[0].body.Features.Lidarr.Url, '');
    assert.equal(h.calls[0].body.Features.Readarr.Key, null);
  } finally { h.close(); }
});

test('background administrator revalidation preserves drafts on revision conflict and clears them on lost access', async () => {
  const h = await harness();
  try {
    h.change('discordToken', 'private-draft'); h.control.saved.Revision = 1;
    await h.ui.verifyAccess(); assert.equal(h.form.elements.discordToken.value, 'private-draft');
    assert.equal(h.form.querySelector('[type="submit"]').disabled, true);
    assert.match(h.view.querySelector('[data-settings-notice]').textContent, /changed elsewhere/);
    h.control.reply = async () => { throw Object.assign(new Error('Denied'), { status: 403 }); };
    await h.ui.verifyAccess(); assert.equal(h.view.querySelector('[data-settings]').hidden, true); assert.equal(h.form.elements.discordToken.value, '');
  } finally { h.close(); }
});
