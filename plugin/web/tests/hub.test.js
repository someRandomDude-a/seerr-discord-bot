import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import vm from 'node:vm';
import { JSDOM } from 'jsdom';

async function harness(ticket, configure) {
  const html = await readFile(new URL('../../src/Jellyfin.Plugin.MediaHub/Web/hub.html', import.meta.url), 'utf8');
  const js = await readFile(new URL('../../src/Jellyfin.Plugin.MediaHub/Web/hub.js', import.meta.url), 'utf8');
  const dom = new JSDOM(html, { pretendToBeVisual: true, url: 'https://jellyfin.example/web/index.html#!/configurationpage?name=mediahub' + (ticket ? '&hubTicket=' + ticket : '') });
  const view = dom.window.document.querySelector('#mediaHubPage');
  const calls = [], subscriptions = [], timers = [];
  const control = { user: 'viewer-123' };
  configure?.({ dom, control });
  const ApiClient = {
    getCurrentUserId: () => control.user,
    getUrl: path => '/' + path,
    subscribe: (types, callback) => { const subscription = { types, callback, closed: false }; subscriptions.push(subscription); return () => { subscription.closed = true; }; },
    ajax: async options => {
      const body = options.data ? JSON.parse(options.data) : null;
      calls.push({ url: options.url, body });
      if (control.reply) { const result = await control.reply(options, body); if (result !== undefined) return result; }
      if (options.url.endsWith('/Settings')) throw { status: 403 };
      if (options.url.endsWith('/Status')) return { Linked: true, Party: { Active: true, Id: 'party', GroupName: 'Private group', State: 'Idle', Participants: ['Host'] } };
      if (options.url.endsWith('/Me')) return { Revision: 0, Preferences: { JellyfinNotifications: true, DiscordNotifications: false, Muted: false }, Watchlist: [] };
      if (options.url.endsWith('/Deletions') || options.url.endsWith('/Me/Consent')) return [];
      return { GroupId: 'group-123', Confirmed: false };
    }
  };
  const context = vm.createContext({ document: dom.window.document, window: dom.window, URL, URLSearchParams, console, ApiClient,
    setInterval: fn => { timers.push(fn); return fn; }, clearInterval() {},
    setTimeout: fn => { timers.push(fn); return fn; }, clearTimeout() {} });
  vm.runInContext(js, context);
  dom.window.JellyfinMediaHub(view, ticket ? { hubTicket: ticket } : {});
  const settle = () => new Promise(resolve => setImmediate(resolve));
  view.dispatchEvent(new dom.window.Event('viewshow')); await settle(); await settle();
  return { dom, view, calls, control, subscriptions, timers, settle, close: () => dom.window.close() };
}

test('plugin pages inherit Jellyfin components without independent fonts, themes, or CSS colors', async () => {
  const h = await harness();
  try {
    assert.ok(h.view.querySelector('input[is="emby-input"]'));
    assert.ok(h.view.querySelector('.button-submit.emby-button'));
    assert.equal(h.view.querySelector('style,link[rel="stylesheet"]'), null);
    assert.equal(h.view.querySelector('[data-appearance],[data-theme]'), null);
    assert.equal(h.view.querySelector('[data-settings]').hidden, true);
    assert.match(h.view.querySelector('[data-party]').textContent, /Private group/); // Optional settings 403 does not clear verified viewer state.
  } finally { h.close(); }
});

test('setup tutorial is bundled, administrator-only and describes the plugin installation', async () => {
  const h = await harness();
  try {
    const guide = h.view.querySelector('[data-onboarding]');
    assert.ok(guide);
    assert.equal(guide.hidden, true);
    assert.equal(guide.closest('section').hidden, true);
    assert.equal(h.view.querySelector('[data-settings]').hidden, true);
    assert.equal(guide.querySelectorAll('[data-setup-step]').length, 10);
    for (const phrase of ['Guild Install', 'applications.commands', 'Do not grant Administrator', 'Copy Server ID', 'Save plugin settings', 'Save integrations', 'Message Content', 'Server Members', 'shared private DM inbox', 'Confirm these accounts', 'URL Mappings', 'App Launcher', 'Check link', 'automatic deletion off']) {
      assert.ok(guide.textContent.includes(phrase), `Missing beginner guidance: ${phrase}`);
    }
    assert.match(guide.textContent, /native plugin does not register \/activity/);
    assert.match(guide.textContent, /media\.example\/jellyfin\/MediaHub\/Activity\//);
    assert.match(guide.textContent, /https:\/\/127\.0\.0\.1/);
    assert.match(guide.textContent, /The plugin runs inside Jellyfin/);
  } finally { h.close(); }
});

test('tutorial instructions do not automatically load remote content or grant viewer setup access', async () => {
  const h = await harness();
  try {
    const guide = h.view.querySelector('[data-onboarding]'); const calls = h.calls.length;
    h.view.querySelector('[data-tutorial-start]').click(); h.view.querySelector('[data-tutorial-restart]').click();
    await h.settle();
    assert.equal(h.calls.length, calls);
    assert.equal(h.calls.some(c => c.body), false);
    assert.equal(guide.hidden, true);
    assert.equal(guide.querySelector('script,img,iframe,style,link'), null);
    for (const link of guide.querySelectorAll('a')) {
      assert.equal(new URL(link.href).protocol, 'https:');
      assert.ok(link.relList.contains('noopener')); assert.ok(link.relList.contains('noreferrer'));
    }
  } finally { h.close(); }
});

const setupKey = 'mediahub.setup.v1.' + JSON.stringify(['viewer123@', '/web/index.html']);
async function setupHarness(revision = 0, configure) {
  let settings = { Revision: revision, DiscordEnabled: false, AutomaticDeletion: false, PublicJellyfinUrl: '', AllowedGuilds: [], SeerrUrl: '', HasDiscordToken: false, HasSeerrKey: false,
    RecentActivityDays: 14, DeletionDelayDays: 2, Features: { ActivityEnabled: false, Operators: [], ArrDestinations: [] } };
  return harness(undefined, ({ dom, control }) => {
    control.reply = async (options, body) => {
      if (!options.url.endsWith('/Settings')) return undefined;
      if (body) {
        const next = { ...settings, Revision: settings.Revision + 1 };
        for (const key of ['DiscordEnabled', 'AutomaticDeletion', 'PublicJellyfinUrl', 'AllowedGuilds', 'SeerrUrl', 'RecentActivityDays', 'DeletionDelayDays']) if (key in body) next[key] = body[key];
        if (body.DiscordToken) next.HasDiscordToken = true;
        if (body.SeerrKey) next.HasSeerrKey = true;
        if (body.Features) next.Features = { ...settings.Features, ActivityEnabled: body.Features.ActivityEnabled, DiscordApplicationId: body.Features.DiscordApplicationId,
          Operators: body.Features.Operators, MessageContent: body.Features.MessageContent, MembersIntent: body.Features.MembersIntent, ArrDestinations: body.Features.ArrDestinations };
        settings = next;
      }
      return structuredClone(settings);
    };
    configure?.({ dom, control });
  });
}
const tutorialStep = h => [...h.view.querySelectorAll('[data-setup-step]')].filter(step => !step.hidden).map(step => step.dataset.setupStep);
const tutorialNext = h => h.view.querySelector('[data-tutorial-next]').click();

test('fresh installation automatically starts a single-step setup tutorial with no configuration writes', async () => {
  const h = await setupHarness();
  try {
    assert.equal(h.view.querySelector('[data-tutorial-section]').hidden, false);
    assert.equal(h.view.querySelector('[data-onboarding]').hidden, false);
    assert.equal(h.view.querySelector('[data-hub-workspace]').hidden, true);
    assert.deepEqual(tutorialStep(h), ['page']);
    assert.match(h.view.querySelector('[data-tutorial-progress]').textContent, /Step 1 of 10/);
    assert.equal(h.view.querySelector('[data-tutorial-back]').disabled, true);
    assert.equal(h.calls.some(c => c.body), false);
    assert.deepEqual(JSON.parse(h.dom.window.localStorage.getItem(setupKey)), { Version: 1, Step: 'page', Completed: false });
  } finally { h.close(); }
});

test('tutorial embeds the real form and requires explicit save before leaving a configuration step', async () => {
  const h = await setupHarness();
  try {
    const form = h.view.querySelector('[data-config]');
    for (let n = 0; n < 4; n++) tutorialNext(h);
    assert.deepEqual(tutorialStep(h), ['configure']);
    assert.equal(form.parentElement.dataset.tutorialSlot, 'config');
    assert.equal(h.view.querySelectorAll('[data-config]').length, 1);
    assert.equal(h.view.querySelector('[data-tutorial-next]').disabled, true);
    form.elements.public.value = 'https://configured.example/base'; form.elements.guilds.value = '555';
    form.elements.token.value = 'private-new-bot-token'; form.elements.seerr.value = 'http://seerr'; form.elements.key.value = 'private-seerr-key';
    form.dispatchEvent(new h.dom.window.Event('input', { bubbles: true })); tutorialNext(h);
    assert.equal(h.calls.some(c => c.body), false);
    form.dispatchEvent(new h.dom.window.Event('submit', { cancelable: true })); await h.settle();
    assert.equal(h.calls.filter(c => c.url.endsWith('/Settings') && c.body).length, 1);
    assert.equal(form.elements.token.value, ''); assert.equal(form.elements.key.value, '');
    assert.equal(h.view.querySelector('[data-tutorial-next]').disabled, false);
    assert.doesNotMatch(h.dom.window.localStorage.getItem(setupKey), /private|configured|seerr|555/);
    assert.match(h.view.querySelector('[data-tutorial-notice]').textContent, /Settings saved/);
    tutorialNext(h);
    assert.deepEqual(tutorialStep(h), ['intents']);
    assert.equal(form.parentElement.dataset.tutorialHome, 'config');
    assert.equal(h.view.querySelector('[data-features]').parentElement.dataset.tutorialSlot, 'messaging');
    for (const group of h.view.querySelectorAll('[data-feature-group]')) assert.equal(group.hidden, group.dataset.featureGroup !== 'messaging');
  } finally { h.close(); }
});

test('tutorial provides Activity and optional-service fields in place and never enables them on Next', async () => {
  const h = await setupHarness(4);
  try {
    assert.equal(h.view.querySelector('[data-onboarding]').hidden, true);
    h.view.querySelector('[data-tutorial-restart]').click();
    for (let n = 0; n < 6; n++) tutorialNext(h);
    assert.deepEqual(tutorialStep(h), ['activity']);
    const form = h.view.querySelector('[data-features]');
    assert.equal(form.parentElement.dataset.tutorialSlot, 'activity');
    assert.equal(form.elements.activityEnabled.checked, false);
    form.elements.applicationId.value = '123456'; form.elements.clientSecret.value = 'private-oauth-secret';
    form.dispatchEvent(new h.dom.window.Event('change', { bubbles: true }));
    assert.equal(h.view.querySelector('[data-tutorial-next]').disabled, true);
    assert.equal(h.calls.some(c => c.body), false);
    form.dispatchEvent(new h.dom.window.Event('submit', { cancelable: true })); await h.settle();
    assert.equal(h.calls.filter(c => c.body).length, 1);
    assert.equal(h.calls.find(c => c.body).body.Features.ActivityEnabled, false);
    assert.equal(form.elements.clientSecret.value, '');
    tutorialNext(h); assert.deepEqual(tutorialStep(h), ['optional-services']);
    assert.equal(form.parentElement.dataset.tutorialSlot, 'services');
    tutorialNext(h); assert.deepEqual(tutorialStep(h), ['link']);
    assert.equal(h.view.querySelector('[data-link]').closest('[data-tutorial-slot]').dataset.tutorialSlot, 'link');
    assert.doesNotMatch(h.dom.window.localStorage.getItem(setupKey), /private|123456/);
  } finally { h.close(); }
});

test('pause, resume, Back and restart keep drafts and never reset or save configuration', async () => {
  const h = await setupHarness(2);
  try {
    h.view.querySelector('[data-tutorial-start]').click();
    for (let n = 0; n < 4; n++) tutorialNext(h);
    const form = h.view.querySelector('[data-config]'); form.elements.token.value = 'unsaved-secret';
    h.view.querySelector('[data-tutorial-back]').click(); assert.deepEqual(tutorialStep(h), ['ids']);
    tutorialNext(h); assert.deepEqual(tutorialStep(h), ['configure']);
    h.view.querySelector('[data-tutorial-pause]').click();
    assert.equal(h.view.querySelector('[data-hub-workspace]').hidden, false);
    assert.equal(form.parentElement.dataset.tutorialHome, 'config'); assert.equal(form.elements.token.value, 'unsaved-secret');
    h.view.querySelector('[data-tutorial-start]').click(); assert.deepEqual(tutorialStep(h), ['configure']);
    h.view.querySelector('[data-tutorial-restart]').click(); assert.deepEqual(tutorialStep(h), ['page']);
    assert.equal(form.elements.token.value, 'unsaved-secret');
    assert.equal(h.calls.some(c => c.body), false);
    assert.doesNotMatch(h.dom.window.localStorage.getItem(setupKey), /unsaved-secret/);
  } finally { h.close(); }
});

test('completion remembers only guidance and remains restartable without changing saved settings', async () => {
  const h = await setupHarness(5);
  try {
    h.view.querySelector('[data-tutorial-start]').click();
    for (let n = 0; n < 9; n++) tutorialNext(h);
    assert.deepEqual(tutorialStep(h), ['troubleshooting']);
    assert.equal(h.view.querySelector('[data-tutorial-next]').textContent, 'Finish tutorial');
    tutorialNext(h);
    assert.equal(h.view.querySelector('[data-onboarding]').hidden, true);
    assert.match(h.view.querySelector('[data-tutorial-result]').textContent, /Tutorial complete/);
    assert.deepEqual(JSON.parse(h.dom.window.localStorage.getItem(setupKey)), { Version: 1, Step: 'troubleshooting', Completed: true });
    h.view.dispatchEvent(new h.dom.window.Event('viewhide')); h.view.dispatchEvent(new h.dom.window.Event('viewshow')); await h.settle(); await h.settle();
    assert.equal(h.view.querySelector('[data-onboarding]').hidden, true);
    h.view.querySelector('[data-tutorial-restart]').click(); assert.deepEqual(tutorialStep(h), ['page']);
    assert.equal(JSON.parse(h.dom.window.localStorage.getItem(setupKey)).Completed, false);
    assert.equal(h.calls.some(c => c.body), false);
  } finally { h.close(); }
});

test('unfinished tutorial resumes after navigation and corrupt browser progress is harmless', async () => {
  const h = await setupHarness();
  try {
    tutorialNext(h); tutorialNext(h);
    h.view.dispatchEvent(new h.dom.window.Event('viewhide')); h.view.dispatchEvent(new h.dom.window.Event('viewshow')); await h.settle(); await h.settle();
    assert.deepEqual(tutorialStep(h), ['permissions']);
    assert.equal(h.calls.some(c => c.body), false);
  } finally { h.close(); }
  for (const bad of ['{"Version":1,"Step":"<img onerror=evil>","Completed":false}', 'not-json', 'x'.repeat(300)]) {
    const corrupt = await setupHarness(0, ({ dom }) => dom.window.localStorage.setItem(setupKey, bad));
    try { assert.deepEqual(tutorialStep(corrupt), ['page']); assert.equal(corrupt.view.querySelector('[data-onboarding] img'), null); assert.equal(corrupt.calls.some(c => c.body), false); }
    finally { corrupt.close(); }
  }
});

test('tutorial works without browser storage and never appears in Activity', async () => {
  const h = await setupHarness(0, ({ dom }) => Object.defineProperty(dom.window, 'localStorage', { get() { throw new Error('Blocked'); } }));
  try { assert.deepEqual(tutorialStep(h), ['page']); tutorialNext(h); assert.deepEqual(tutorialStep(h), ['application']); assert.equal(h.calls.some(c => c.body), false); }
  finally { h.close(); }
  const activity = await setupHarness(0, ({ dom }) => { dom.window.MediaHubNativeUrl = 'https://jellyfin.example/base/web/index.html'; });
  try { assert.equal(activity.view.querySelector('[data-tutorial-section]').hidden, true); assert.equal(activity.dom.window.localStorage.length, 0); }
  finally { activity.close(); }
});

test('identity changes close the tutorial, restore forms and clear unsaved credentials', async () => {
  const h = await setupHarness();
  try {
    for (let n = 0; n < 4; n++) tutorialNext(h);
    h.view.querySelector('#hubToken').value = 'private-draft'; h.control.user = 'another-user';
    h.view.querySelector('[data-tutorial-restart]').click();
    assert.equal(h.view.querySelector('[data-tutorial-section]').hidden, true);
    assert.equal(h.view.querySelector('#hubToken').value, '');
    assert.equal(h.view.querySelector('[data-config]').parentElement.dataset.tutorialHome, 'config');
    assert.equal(h.calls.some(c => c.body), false);
    assert.doesNotMatch(h.dom.window.localStorage.getItem(setupKey), /private-draft|another-user/);
  } finally { h.close(); }
});

test('a failed settings save stays in its tutorial step and leaves the draft available for correction', async () => {
  const h = await setupHarness(0, ({ control }) => {
    const original = control.reply;
    control.reply = async (options, body) => {
      if (options.url.endsWith('/Settings') && body) throw { status: 400, responseJSON: { Error: 'Check the service address.' } };
      return original(options, body);
    };
  });
  try {
    for (let n = 0; n < 4; n++) tutorialNext(h);
    const form = h.view.querySelector('[data-config]'); form.elements.token.value = 'unsaved-correction';
    form.dispatchEvent(new h.dom.window.Event('submit', { cancelable: true })); await h.settle();
    assert.deepEqual(tutorialStep(h), ['configure']);
    assert.equal(form.elements.token.value, 'unsaved-correction');
    assert.equal(h.view.querySelector('[data-tutorial-next]').disabled, true);
    assert.match(h.view.querySelector('[data-tutorial-notice]').textContent, /Check the service address/);
    assert.equal(h.calls.filter(c => c.body).length, 1);
    assert.doesNotMatch(h.dom.window.localStorage.getItem(setupKey), /unsaved-correction/);
  } finally { h.close(); }
});

test('returning OAuth confirmation is brought into the resumed tutorial without automatic approval', async () => {
  const h = await setupHarness(4, ({ dom, control }) => {
    dom.window.localStorage.setItem(setupKey, JSON.stringify({ Version: 1, Step: 'activity', Completed: false }));
    dom.window.sessionStorage.setItem('mediahub.handoff', 'a'.repeat(48));
    const original = control.reply;
    control.reply = async (options, body) => options.url.endsWith('/Link/Handoff/Status') ? { State: 'proved', Discord: '42', Jellyfin: 'Owner' } : original(options, body);
  });
  try {
    assert.deepEqual(tutorialStep(h), ['link']);
    assert.equal(h.view.querySelector('[data-handoff-controls]').closest('[data-tutorial-slot]').dataset.tutorialSlot, 'link');
    assert.match(h.view.querySelector('[data-handoff-controls] button').textContent, /Confirm these accounts/);
    assert.equal(h.calls.some(c => c.url.endsWith('/Complete')), false);
    assert.equal(h.calls.some(c => c.url.endsWith('/Settings') && c.body), false);
  } finally { h.close(); }
});

test('finishing is blocked by unsaved drafts without discarding them', async () => {
  const h = await setupHarness(4, ({ dom }) => dom.window.localStorage.setItem(setupKey, JSON.stringify({ Version: 1, Step: 'troubleshooting', Completed: false })));
  try {
    h.view.querySelector('[data-tutorial-pause]').click();
    const form = h.view.querySelector('[data-features]'); form.elements.clientSecret.value = 'unfinished-secret';
    h.view.querySelector('[data-tutorial-start]').click(); tutorialNext(h);
    assert.deepEqual(tutorialStep(h), ['troubleshooting']);
    assert.equal(h.view.querySelector('[data-tutorial-next]').disabled, true);
    assert.equal(JSON.parse(h.dom.window.localStorage.getItem(setupKey)).Completed, false);
    assert.equal(form.elements.clientSecret.value, 'unfinished-secret');
    assert.equal(h.calls.some(c => c.body), false);
  } finally { h.close(); }
});

test('restart does not hide another integrations draft that an explicit Save would submit', async () => {
  const h = await setupHarness(4);
  try {
    const form = h.view.querySelector('[data-features]'); form.elements.clientSecret.value = 'review-this-draft';
    h.view.querySelector('[data-tutorial-restart]').click();
    for (let n = 0; n < 5; n++) tutorialNext(h);
    assert.deepEqual(tutorialStep(h), ['intents']);
    assert.equal(form.elements.clientSecret.closest('[data-feature-group]').hidden, false);
    assert.equal(h.view.querySelector('[data-tutorial-next]').disabled, true);
    assert.equal(form.elements.clientSecret.value, 'review-this-draft');
    assert.equal(h.calls.some(c => c.body), false);
  } finally { h.close(); }
});

test('sensitive configuration inputs have attached explanations and setup terminology stays separate', async () => {
  const h = await harness();
  try {
    for (const id of ['hubPublic', 'hubGuilds', 'hubToken', 'hubSeerr', 'hubOperators', 'hubDiscordApplication', 'hubDiscordSecret']) {
      const input = h.view.querySelector('#' + id); const help = h.view.querySelector('#' + input.getAttribute('aria-describedby'));
      assert.ok(help?.textContent.trim(), `${id} needs an associated explanation`);
    }
    assert.match(h.view.querySelector('#hubGuildsHelp').textContent, /Blank is not allow-all/);
    assert.match(h.view.querySelector('#hubTokenHelp').textContent, /not the Application ID or OAuth client secret/);
    assert.equal(h.view.querySelector('#hubToken').type, 'password');
    assert.equal(h.view.querySelector('#hubDiscordSecret').type, 'password');
    assert.equal(h.view.querySelector('#hubToken').value, '');
    assert.equal(h.view.querySelector('#hubDiscordSecret').value, '');
    const ids = [...h.view.querySelectorAll('[id]')].map(node => node.id);
    assert.equal(new Set(ids).size, ids.length);
  } finally { h.close(); }
});

test('joining uses the authenticated native player and waits for its actual GroupJoined event', async () => {
  const h = await harness('account-bound-ticket');
  try {
    const joins = h.calls.filter(call => call.url.endsWith('/Parties/Join'));
    assert.equal(joins.length, 1); assert.equal(joins[0].body.Ticket, 'account-bound-ticket');
    assert.match(h.view.querySelector('[data-notice]').textContent, /Waiting for Jellyfin confirmation/);
    assert.doesNotMatch(h.dom.window.location.href, /account-bound-ticket/);
    h.subscriptions[0].callback({ Data: { Type: 'GroupJoined', Data: { GroupId: 'group-123' } } });
    assert.equal(h.view.querySelector('[data-notice]').textContent, 'Joined the watch party.');
    assert.doesNotMatch(h.view.textContent, /account-bound-ticket/);
  } finally { h.close(); }
});

test('a native denial is not mistaken for HTTP join success', async () => {
  const h = await harness('ticket');
  try {
    h.subscriptions[0].callback({ Data: { Type: 'LibraryAccessDenied' } });
    assert.match(h.view.querySelector('[data-notice]').textContent, /access was denied/);
    assert.equal(h.calls.filter(call => call.url.endsWith('/Parties/Join')).length, 1);
  } finally { h.close(); }
});

test('state refresh preserves form drafts and removes private content on lost access', async () => {
  const h = await harness();
  try {
    const draft = h.view.querySelector('#hubPartyName'); draft.value = 'Unsaved party name';
    h.timers[0](); await h.settle(); assert.equal(h.view.querySelector('#hubPartyName'), draft); assert.equal(draft.value, 'Unsaved party name');
    h.control.reply = async options => { if (options.url.endsWith('/Status')) throw { status: 401 }; };
    h.timers[0](); await h.settle();
    assert.equal(h.view.querySelector('[data-party]').textContent, '');
    assert.equal(h.view.querySelector('[data-close]').hidden, true);
  } finally { h.close(); }
});

test('leaving the plugin page closes the websocket subscription', async () => {
  const h = await harness();
  try { h.view.dispatchEvent(new h.dom.window.Event('viewhide')); assert.equal(h.subscriptions[0].closed, true); }
  finally { h.close(); }
});

test('nested Plugin Pages and dashboard lifecycle events cannot join twice', async () => {
  const h = await harness('one-use-ticket');
  try {
    h.view.dispatchEvent(new h.dom.window.Event('viewshow')); await h.settle();
    assert.equal(h.calls.filter(call => call.url.endsWith('/Parties/Join')).length, 1);
    assert.equal(h.subscriptions.length, 1);
  } finally { h.close(); }
});

const personal = (Revision, Watchlist = [], Preferences = { JellyfinNotifications: true, DiscordNotifications: false, Muted: false }) => ({ Revision, Watchlist, Preferences });
const itemId = '11111111222233334444555555555555';
const savedItem = Subscribed => ({ ItemId: itemId, Title: '<img src=x onerror=alert(1)>', Kind: 'Movie', Subscribed });
const submit = (h, selector) => h.view.querySelector(selector).dispatchEvent(new h.dom.window.Event('submit', { cancelable: true }));
const poll = async h => { h.timers[0](); await h.settle(); await h.settle(); };

const mediaCard = { Kind: 'tv', ExternalId: 123, NativeId: null, Title: '<img src=x onerror=alert(1)>', Overview: '<script>private</script>', Status: 'Downloading', Seasons: [0, 1, 2] };
test('catalog browsing is passive, details are explicit, and metadata is rendered as text', async () => {
  const h = await harness();
  try {
    h.control.reply = async options => options.url.includes('/Catalog/Browse?') ? { Items: [mediaCard], Page: 1, Pages: 2 } : options.url.endsWith('/tv/123/Details') ? mediaCard : undefined;
    h.view.querySelector('[data-catalog-query]').elements.source.value = 'browse';
    h.view.querySelector('[data-catalog-query]').elements.kind.value = 'tv';
    submit(h, '[data-catalog-query]'); await h.settle();
    assert.equal(h.calls.filter(call => call.body).length, 0);
    assert.equal(h.view.querySelector('[data-catalog-list] img'), null);
    h.view.querySelector('[data-catalog-list] button').click(); await h.settle();
    assert.equal(h.calls.filter(call => call.url.endsWith('/tv/123/Details')).length, 1);
    assert.equal(h.view.querySelector('[data-media-details] script'), null);
    assert.match(h.view.querySelector('[data-media-details]').textContent, /<script>private/);
    assert.equal(h.view.querySelector('[data-media-details] a'), null);
  } finally { h.close(); }
});
test('request preparation is not confirmation and displays its immutable edition and season selection', async () => {
  const h = await harness();
  try {
    let finishPrepare;
    h.control.reply = async (options, body) => {
      if (options.url.includes('/Catalog/Browse?')) return { Items: [mediaCard], Page: 1, Pages: 1 };
      if (options.url.endsWith('/tv/123/Details')) return mediaCard;
      if (options.url.endsWith('/Requests/Prepare')) return new Promise(resolve => { finishPrepare = resolve; });
      if (options.url.endsWith('/Requests/plan/Confirm')) return { State: 'uncertain' };
    };
    h.view.querySelector('[data-catalog-query]').elements.source.value = 'browse'; submit(h, '[data-catalog-query]'); await h.settle();
    h.view.querySelector('[data-catalog-list] button').click(); await h.settle();
    const panel = h.view.querySelector('[data-media-details]'); const checkboxes = panel.querySelectorAll('input');
    checkboxes[0].checked = true; checkboxes[2].checked = true;
    [...panel.querySelectorAll('button')].find(button => button.textContent === 'Prepare request').click(); await h.settle();
    const prepared = h.calls.find(call => call.url.endsWith('/Requests/Prepare')); assert.equal(prepared.body.Is4k, true); assert.deepEqual(prepared.body.Seasons, [1]);
    assert.equal(h.calls.some(call => call.url.endsWith('/Confirm')), false);
    checkboxes[0].checked = false; checkboxes[2].checked = false;
    finishPrepare({ Id: 'plan', Title: 'A title' }); await h.settle();
    assert.match(panel.querySelector('[data-request-confirmation]').textContent, /4K · Seasons 1/);
    const confirm = panel.querySelector('[data-request-confirmation] button'); confirm.click(); confirm.click(); await h.settle();
    assert.equal(h.calls.filter(call => call.url.endsWith('/Requests/plan/Confirm')).length, 1);
    assert.match(panel.textContent, /Not confirmed/); assert.doesNotMatch(panel.textContent, /Requested\./);
    await poll(h); assert.equal(h.calls.filter(call => call.url.endsWith('/Requests/plan/Confirm')).length, 1);
  } finally { h.close(); }
});
test('catalog responses cannot disclose results after an account switch', async () => {
  const h = await harness();
  try {
    let finish;
    h.control.reply = async options => options.url.includes('/Catalog/Library?') ? new Promise(resolve => { finish = resolve; }) : undefined;
    submit(h, '[data-catalog-query]'); await h.settle(); h.control.user = 'another-user';
    finish({ Items: [mediaCard], Page: 1, Pages: 1 }); await h.settle();
    assert.equal(h.view.querySelector('[data-catalog-list]').children.length, 0);
    assert.equal(h.view.querySelector('[data-media-details]').children.length, 0);
  } finally { h.close(); }
});

test('OAuth resume stores only an opaque handoff and never completes without an explicit account confirmation', async () => {
  const code = 'a'.repeat(48);
  const h = await harness(undefined, ({ dom, control }) => {
    dom.window.sessionStorage.setItem('mediahub.handoff', code);
    control.reply = async options => options.url.endsWith('/Link/Handoff/Status') ? { State: 'proved', Discord: '<script>42</script>', Jellyfin: 'Viewer' } : undefined;
  });
  try {
    assert.equal(h.calls.some(c => c.url.endsWith('/Complete')), false);
    assert.match(h.view.querySelector('[data-handoff-notice]').textContent, /<script>42/);
    assert.equal(h.view.querySelector('[data-handoff-controls] script'), null);
    assert.equal(h.dom.window.sessionStorage.length, 1);
    h.view.querySelector('[data-handoff-controls] button').click(); await h.settle(); await h.settle();
    assert.equal(h.calls.filter(c => c.url.endsWith('/Complete')).length, 1);
    assert.equal(h.calls.find(c => c.url.endsWith('/Complete')).body.Code, code);
    assert.equal(h.dom.window.sessionStorage.getItem('mediahub.handoff'), null);
  } finally { h.close(); }
});

test('late link status cannot disclose either identity after an account switch', async () => {
  let finish;
  const h = await harness(undefined, ({ dom, control }) => {
    dom.window.sessionStorage.setItem('mediahub.handoff', 'b'.repeat(48));
    control.reply = async options => options.url.endsWith('/Link/Handoff/Status') ? new Promise(resolve => { finish = resolve; }) : undefined;
  });
  try {
    h.control.user = 'someone-else'; finish({ State: 'proved', Discord: 'private-discord', Jellyfin: 'private-native' }); await h.settle();
    assert.equal(h.view.querySelector('[data-handoff-controls]').children.length, 0);
    assert.doesNotMatch(h.view.textContent, /private-discord|private-native/);
    assert.equal(h.calls.some(c => c.url.endsWith('/Complete')), false);
  } finally { h.close(); }
});

test('saving personal notifications preserves the separate host-derived Activity palette', async () => {
  const theme = { Background: '#112233', Text: '#eeeeee', Accent: '#00a4dc', Surface: '#101010' };
  const h = await harness(undefined, ({ control }) => {
    control.reply = async options => options.url.endsWith('/Me') ? personal(0, [], { JellyfinNotifications: true, DiscordNotifications: false, Muted: false, Theme: theme }) : undefined;
  });
  try {
    submit(h, '[data-preferences]'); await h.settle();
    assert.deepEqual(h.calls.find(c => c.url.endsWith('/Me') && c.body).body.Preferences.Theme, theme);
    assert.equal(h.calls.some(c => c.url.endsWith('/Settings') && c.body), false);
  } finally { h.close(); }
});
test('elevated local settings remain reviewable when unsigned integration state blocks ordinary reads', async () => {
  const config = { Revision: 7, DiscordEnabled: false, AutomaticDeletion: false, PublicJellyfinUrl: 'https://review.example/base', AllowedGuilds: [], SeerrUrl: '', ConfigurationReviewRequired: true,
    Features: { ArrDestinations: [{ Service: 'radarr', Instance: 0, Url: 'https://arr.example/base' }] } };
  const h = await harness(undefined, ({ control }) => {
    control.reply = async options => {
      if (options.url.endsWith('/Me')) throw Object.assign(new Error('Unsigned integrations are blocked'), { status: 503 });
      if (options.url.endsWith('/Settings')) return config;
    };
  });
  try {
    assert.equal(h.view.querySelector('[data-settings]').hidden, false);
    assert.match(h.view.querySelector('[data-notice]').textContent, /unsigned settings are blocked/);
    assert.equal(h.view.querySelector('[data-config]').elements.automaticDeletion.checked, false);
    assert.match(h.view.querySelector('[data-features]').elements.arrDestinations.value, /arr.example/);
    assert.equal(h.view.querySelector('[data-personal]').hidden, true);
    assert.equal(h.calls.some(c => c.body), false); // Review never saves or activates anything automatically.
  } finally { h.close(); }
});
test('external watchlist and notification feed keep identity projections private and preserve drafts', async () => {
  const h = await harness();
  try {
    let state = { ...personal(2), ExternalWatchlist: [{ Kind: 'movie', ExternalId: 123, Title: '<img src=x>', Subscribed: true }] };
    h.control.reply = async (options, body) => {
      if (options.url.endsWith('/Me/Notifications')) return [{ Id: 'notice', Title: '<script>x</script>', Status: 'Available', Seen: false }];
      if (options.url.endsWith('/Me/Watchlist/movie/123')) { state = { ...state, Revision: state.Revision + 1, ExternalWatchlist: state.ExternalWatchlist.map(row => ({ ...row, Subscribed: body.Subscribed })) }; return state; }
      if (options.url.endsWith('/Me')) return state;
    };
    const draft = h.view.querySelector('#hubCatalogQuery'); draft.value = 'Unsent search'; await poll(h);
    assert.equal(draft.value, 'Unsent search'); assert.equal(h.view.querySelector('[data-watchlist] img'), null);
    assert.equal(h.view.querySelector('[data-notification-feed] script'), null);
    h.view.querySelector('[data-watchlist] button').click(); await h.settle();
    assert.equal(h.view.querySelector('[data-watchlist] button').getAttribute('aria-pressed'), 'false');
    assert.equal(h.view.querySelectorAll('[data-watchlist] li').length, 1);
  } finally { h.close(); }
});

test('preferences are personal and a remote revision preserves drafts until explicit reload', async () => {
  const h = await harness();
  try {
    const form = h.view.querySelector('[data-preferences]');
    assert.equal(h.view.querySelector('[data-personal]').hidden, false);
    assert.equal(form.elements.discord.checked, false);
    form.elements.discord.checked = true; form.dispatchEvent(new h.dom.window.Event('change'));
    h.control.reply = async options => options.url.endsWith('/Me') ? personal(3, [], { JellyfinNotifications: false, DiscordNotifications: false, Muted: true }) : undefined;
    await poll(h);
    assert.equal(form.elements.discord.checked, true); assert.equal(form.elements.muted.checked, false);
    assert.equal(form.querySelector('[type=submit]').disabled, true);
    assert.match(h.view.querySelector('[data-personal-notice]').textContent, /draft was kept/);
    submit(h, '[data-preferences]'); await h.settle();
    assert.equal(h.calls.filter(call => call.url.endsWith('/Me') && call.body).length, 0);
    h.view.querySelector('[data-reload-preferences]').click();
    assert.equal(form.elements.discord.checked, false); assert.equal(form.elements.muted.checked, true);
    assert.equal(form.querySelector('[type=submit]').disabled, false);
  } finally { h.close(); }
});

test('saving preferences uses the confirmed revision without changing administrator settings', async () => {
  const h = await harness();
  try {
    h.control.reply = async (options, body) => options.url.endsWith('/Me') && body ? personal(1, [], body.Preferences) : undefined;
    const form = h.view.querySelector('[data-preferences]');
    form.elements.discord.checked = true; form.dispatchEvent(new h.dom.window.Event('change'));
    submit(h, '[data-preferences]'); submit(h, '[data-preferences]'); await h.settle();
    const writes = h.calls.filter(call => call.body);
    assert.equal(writes.length, 1); assert.equal(writes[0].url, '/MediaHub/Me');
    assert.equal(writes[0].body.Revision, 0); assert.equal(writes[0].body.Preferences.DiscordNotifications, true);
    assert.equal(form.querySelector('[type=submit]').disabled, false);
  } finally { h.close(); }
});

test('watchlist bells and explicit removal use one scoped transaction and titles are text, not HTML', async () => {
  const h = await harness();
  try {
    let state = personal(0);
    h.control.reply = async (options, body) => {
      if (options.url.endsWith('/Bell')) { state = personal(state.Revision + 1, [savedItem(body.Subscribed)]); return state; }
      if (options.url.endsWith('/Remove')) { state = personal(state.Revision + 1); return state; }
      if (options.url.endsWith('/Me')) return state;
    };
    h.view.querySelector('#hubItem').value = itemId; submit(h, '[data-follow]'); await h.settle();
    assert.equal(h.view.querySelector('[data-watchlist] img'), null);
    assert.match(h.view.querySelector('[data-watchlist]').textContent, /<img src=x/);
    const on = h.view.querySelector('[data-watchlist] button'); assert.equal(on.getAttribute('aria-pressed'), 'true');
    on.click(); await h.settle();
    assert.equal(h.view.querySelectorAll('[data-watchlist] li').length, 1);
    assert.equal(h.view.querySelector('[data-watchlist] button').getAttribute('aria-pressed'), 'false');
    h.view.querySelectorAll('[data-watchlist] button')[1].click(); await h.settle();
    assert.equal(h.view.querySelectorAll('[data-watchlist] li').length, 0);
    assert.deepEqual(h.calls.filter(c => c.body).map(c => c.body.Revision), [0, 1, 2]);
  } finally { h.close(); }
});

test('late personal read cannot overwrite a newer bell mutation', async () => {
  const h = await harness();
  try {
    let finishRead;
    h.control.reply = async (options, body) => {
      if (options.url.endsWith('/Me') && !body) return new Promise(resolve => { finishRead = resolve; });
      if (options.url.endsWith('/Bell')) return personal(1, [savedItem(true)]);
    };
    h.timers[0](); await h.settle();
    h.view.querySelector('#hubItem').value = itemId; submit(h, '[data-follow]'); await h.settle();
    finishRead(personal(0)); await h.settle();
    assert.equal(h.view.querySelector('[data-watchlist] button').getAttribute('aria-pressed'), 'true');
  } finally { h.close(); }
});

test('uncertain personal mutation rereads current state but is never automatically replayed', async () => {
  const h = await harness();
  try {
    h.control.reply = async options => {
      if (options.url.endsWith('/Bell')) throw { status: 503 };
      if (options.url.endsWith('/Me')) return personal(1, [savedItem(true)]);
    };
    h.view.querySelector('#hubItem').value = itemId; submit(h, '[data-follow]'); await h.settle(); await h.settle();
    assert.equal(h.calls.filter(c => c.url.endsWith('/Bell')).length, 1);
    assert.equal(h.view.querySelector('[data-watchlist] button').getAttribute('aria-pressed'), 'true');
    assert.match(h.view.querySelector('[data-notice]').textContent, /not confirmed/);
  } finally { h.close(); }
});

test('account switch and page navigation reject late personal responses', async () => {
  for (const action of ['switch', 'hide']) {
    const h = await harness();
    try {
      let finish;
      h.control.reply = async options => options.url.endsWith('/Me') ? new Promise(resolve => { finish = resolve; }) : undefined;
      h.timers[0](); await h.settle();
      if (action === 'switch') h.control.user = 'different-viewer';
      else h.view.dispatchEvent(new h.dom.window.Event('viewhide'));
      finish(personal(4, [savedItem(true)])); await h.settle(); await h.settle();
      assert.equal(h.view.querySelector('[data-personal]').hidden, true);
      assert.equal(h.view.querySelectorAll('[data-watchlist] li').length, 0);
      assert.equal(h.subscriptions[0].closed, true);
    } finally { h.close(); }
  }
});

test('failed live verification clears personal state even when an older read completes later', async () => {
  const h = await harness();
  try {
    h.control.reply = async options => {
      if (options.url.endsWith('/Status')) throw { status: 401 };
      if (options.url.endsWith('/Me')) return personal(3, [savedItem(true)]);
    };
    await poll(h);
    assert.equal(h.view.querySelector('[data-personal]').hidden, true);
    assert.equal(h.view.querySelectorAll('[data-watchlist] li').length, 0);
  } finally { h.close(); }
});

test('shared queue uses anonymous server projections and owner-only Undo controls', async () => {
  const h = await harness();
  try {
    h.control.reply = async options => options.url.endsWith('/Deletions') ? [
      { Id: 'own', Title: '<img src=x>', State: 'queued', Owner: 'Yours', CanUndo: true },
      { Id: 'other', Title: 'Other title', State: 'waiting', Owner: 'Anonymous', CanUndo: false }
    ] : undefined;
    await poll(h);
    assert.equal(h.view.querySelector('[data-deletions]').hidden, false);
    assert.equal(h.view.querySelector('[data-deletion-list] img'), null);
    assert.match(h.view.querySelector('[data-deletion-list]').textContent, /Anonymous/);
    assert.equal(h.view.querySelectorAll('[data-deletion-list] button').length, 1);
    h.view.querySelector('[data-deletion-list] button').click(); await h.settle();
    assert.ok(h.calls.some(call => call.url === '/MediaHub/Deletions/own/Undo'));
    h.view.dispatchEvent(new h.dom.window.Event('viewhide'));
    assert.equal(h.view.querySelector('[data-deletions]').hidden, true);
    assert.equal(h.view.querySelector('[data-deletion-list]').textContent, '');
  } finally { h.close(); }
});

test('consent is explicit and does not record media activity', async () => {
  const h = await harness();
  try {
    h.control.reply = async options => options.url.endsWith('/Me/Consent') ? [{ Id: 'notice', Title: 'Show', Message: 'Allow deletion?' }] : undefined;
    await poll(h);
    const buttons = h.view.querySelectorAll('[data-consent-list] button');
    assert.deepEqual([...buttons].map(button => button.textContent), ['Agree', 'Decline']);
    buttons[1].click(); await h.settle();
    const write = h.calls.find(call => call.url.endsWith('/Me/Consent/notice'));
    assert.equal(write.body.Approve, false);
    assert.equal(h.calls.some(call => call.url.endsWith('/Activity')), false);
  } finally { h.close(); }
});
