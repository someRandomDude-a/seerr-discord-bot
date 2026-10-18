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
      if (options.url.endsWith('/Status')) return { Linked: true, WatchParty: { Available: false, Active: false, Code: 'watchparty_unavailable' } };
      if (options.url.endsWith('/Me')) return { Revision: 0, Preferences: { JellyfinNotifications: true, DiscordNotifications: false, Muted: false }, Watchlist: [] };
      if (options.url.endsWith('/Deletions') || options.url.endsWith('/Me/Consent')) return [];
      return { GroupId: 'group-123', Confirmed: false };
    }
  };
  const context = vm.createContext({ document: dom.window.document, window: dom.window, URL, URLSearchParams, console, ApiClient,
    setInterval: fn => { timers.push(fn); return fn; }, clearInterval() {},
     setTimeout: fn => { timers.push(fn); return fn; }, clearTimeout() {} });
  if (control.watchParty) vm.runInContext(await readFile(new URL('../../src/Jellyfin.Plugin.MediaHub/Web/watchparty.js', import.meta.url), 'utf8'), context);
  vm.runInContext(await readFile(new URL('../../src/Jellyfin.Plugin.MediaHub/Web/settings.js', import.meta.url), 'utf8'), context);
  vm.runInContext(js, context);
  dom.window.JellyfinMediaHub(view, { hubTicket: ticket, hubSection: control.section });
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
    assert.match(h.view.querySelector('[data-watchparty-status]').textContent, /Private SyncPlay provider/);
    assert.equal(h.view.querySelector('[data-personal]').hidden, false); // Optional settings 403 does not clear verified viewer state.
  } finally { h.close(); }
});
test('dedicated watchlist and notification pages show only the selected personal surface and do not load settings', async () => {
  for (const section of ['watchlist', 'notifications']) {
    const h = await harness(undefined, ({ control }) => { control.section = section; });
    try {
      assert.equal(h.view.querySelector('h1').textContent, section === 'watchlist' ? 'Your watchlist' : 'Notifications');
      assert.equal(h.view.querySelector('[data-personal]').hidden, false);
      assert.equal(h.view.querySelector('[data-watchlist-page]').hidden, section !== 'watchlist');
      assert.equal(h.view.querySelector('[data-notifications-page]').hidden, section !== 'notifications');
      assert.equal(h.view.querySelector('[data-preferences-page]').hidden, true);
      assert.equal(h.view.querySelector('[data-settings]').hidden, true);
      assert.equal(h.view.querySelector('[data-catalog]').hidden, true);
      assert.equal(h.view.querySelector('[data-propose]').hidden, true);
      assert.equal(h.calls.some(row => row.url.endsWith('/Settings') || row.body), false);
    } finally { h.close(); }
  }
});

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
test('fresh administrator settings are available directly with no wizard or implicit writes', async () => {
  const h = await setupHarness();
  try {
    assert.equal(h.view.querySelector('[data-settings]').hidden, false);
    assert.equal(h.view.querySelector('[data-onboarding],[data-tutorial-start],[data-config],[data-features]'), null);
    assert.match(h.view.querySelector('[data-setup-summary]').textContent, /No settings saved yet/);
    assert.equal(h.calls.some(c => c.body), false);
    assert.equal(h.dom.window.localStorage.length, 0);
  } finally { h.close(); }
});

test('retired watch-party links are scrubbed without redeeming tickets or subscribing to playback', async () => {
  const h = await harness('account-bound-ticket');
  try {
    assert.equal(h.calls.filter(call => call.url.includes('/Parties')).length, 0);
    assert.match(h.view.querySelector('[data-notice]').textContent, /retired/);
    assert.doesNotMatch(h.dom.window.location.href, /account-bound-ticket/);
    assert.equal(h.subscriptions.length, 0);
    assert.doesNotMatch(h.view.textContent, /account-bound-ticket/);
  } finally { h.close(); }
});

test('retired watch-party links are scrubbed even when live authentication fails', async () => {
  const h = await harness('retired-private-ticket', ({ control }) => {
    control.reply = async options => { if (options.url.endsWith('/Status')) throw { status: 401 }; };
  });
  try {
    assert.doesNotMatch(h.dom.window.location.href, /retired-private-ticket|hubTicket=/);
    assert.doesNotMatch(h.view.textContent, /retired-private-ticket/);
    assert.equal(h.dom.window.localStorage.length, 0);
    assert.equal(h.calls.filter(call => call.url.includes('/Parties')).length, 0);
    assert.equal(h.subscriptions.length, 0);
    assert.equal(h.view.querySelector('[data-personal]').hidden, true);
  } finally { h.close(); }
});

test('optional watch-party permission denial clears that feature without hiding permitted personal media', async () => {
  const h = await harness(null, ({ control }) => {
    control.watchParty = true;
    control.reply = async options => { if (options.url.endsWith('/WatchParty')) throw { status: 403, responseJSON: { Error: 'Watch parties disabled for this account.' } }; };
  });
  try {
    assert.equal(h.view.querySelector('[data-personal]').hidden, false);
    assert.equal(h.view.querySelector('[data-watchparty-create]').hidden, true);
    assert.match(h.view.querySelector('[data-watchparty-status]').textContent, /disabled for this account/);
    assert.equal(h.view.querySelector('[data-watchparty-links] a'), null);
  } finally { h.close(); }
});

test('watch-party bearer navigation is blocked and cleared immediately after a local account switch', async () => {
  const party = '11111111-2222-3333-4444-555555555555', invite = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee';
  const h = await harness(null, ({ control }) => {
    control.watchParty = true;
    control.reply = async options => {
      if (options.url.endsWith('/WatchParty')) return { Available: true, Active: true, Owner: true, PartyId: party, ViewerCount: 1, MaxViewers: 4 };
      if (options.url.endsWith('/Invites')) return { InviteId: invite, PartyId: party, Url: 'https://jellyfin.example/PrivateSyncPlay/Watch#private-link', ExpiresAt: new Date(Date.now() + 600000).toISOString() };
    };
  });
  try {
    h.view.querySelector('[data-watchparty-invite]').click(); await h.settle();
    const a = h.view.querySelector('[data-watchparty-links] a'); assert.ok(a);
    h.control.user = 'different-viewer';
    const event = new h.dom.window.MouseEvent('click', { cancelable: true }); a.dispatchEvent(event);
    assert.equal(event.defaultPrevented, true);
    assert.equal(h.view.querySelector('[data-watchparty-links] a'), null);
    assert.equal(h.view.querySelector('[data-personal]').hidden, true);
  } finally { h.close(); }
});

test('old active-party status cannot restore watch-party actions or disclose participants', async () => {
  const h = await harness(null, ({ control }) => {
    control.reply = async options => options.url.endsWith('/Status') ? { Linked: true, Party: { Active: true, GroupName: 'Old private party', Participants: ['Old private member'] } } : undefined;
  });
  try {
    assert.equal(h.view.querySelector('[data-create],[data-close],[data-party]'), null);
    assert.doesNotMatch(h.view.textContent, /Old private/);
    assert.equal(h.subscriptions.length, 0);
  } finally { h.close(); }
});

test('state refresh preserves form drafts and removes private content on lost access', async () => {
  const h = await harness();
  try {
    const draft = h.view.querySelector('#hubItem'); draft.value = 'Unsaved item ID';
    h.timers[0](); await h.settle(); assert.equal(h.view.querySelector('#hubItem'), draft); assert.equal(draft.value, 'Unsaved item ID');
    h.control.reply = async options => { if (options.url.endsWith('/Status')) throw { status: 401 }; };
    h.timers[0](); await h.settle();
    assert.equal(h.view.querySelector('[data-personal]').hidden, true);
    assert.equal(draft.value, '');
  } finally { h.close(); }
});

test('leaving the plugin page clears personal state without a playback subscription', async () => {
  const h = await harness();
  try { h.view.dispatchEvent(new h.dom.window.Event('viewhide')); assert.equal(h.subscriptions.length, 0); assert.equal(h.view.querySelector('[data-personal]').hidden, true); }
  finally { h.close(); }
});

test('nested Plugin Pages and dashboard lifecycle events never redeem retired invitations', async () => {
  const h = await harness('one-use-ticket');
  try {
    h.view.dispatchEvent(new h.dom.window.Event('viewshow')); await h.settle();
    assert.equal(h.calls.filter(call => call.url.includes('/Parties')).length, 0);
    assert.equal(h.subscriptions.length, 0);
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
      if (options.url.endsWith('/Requests/plan/Receipt')) return { State: 'completed', RequestId: 17 };
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
    assert.match(panel.textContent, /not confirmed/i); assert.doesNotMatch(panel.textContent, /Requested\./);
    await poll(h); assert.equal(h.calls.filter(call => call.url.endsWith('/Requests/plan/Confirm')).length, 1);
    [...panel.querySelectorAll('button')].find(button => button.textContent === 'Refresh request receipt').click(); await h.settle();
    assert.match(panel.textContent, /Request sent/);
    assert.equal(h.calls.filter(call => call.url.endsWith('/Requests/plan/Confirm')).length, 1);
    assert.equal(h.calls.find(call => call.url.endsWith('/Requests/plan/Receipt')).body, null);
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

test('request quotas and Seerr navigation are explicit passive reads, not startup calls or media interactions', async () => {
  const h = await harness();
  try {
    assert.equal(h.calls.some(c => c.url.endsWith('/Me/RequestPermissions') || c.url.endsWith('/SeerrLink')), false);
    h.control.reply = async options => options.url.endsWith('/Me/RequestPermissions') ? { Movies: true, Series: false, Movie4k: false, Series4k: false, MovieQuota: { Limit: 5, Remaining: 3, Days: 7 } } : options.url.endsWith('/SeerrLink') ? { Url: 'https://seerr.example/base' } : undefined;
    h.view.querySelector('[data-request-permissions]').click(); await h.settle();
    assert.match(h.view.querySelector('[data-request-permissions-result]').textContent, /3 remaining of 5 per 7 days/);
    h.view.querySelector('[data-open-seerr]').click(); await h.settle();
    const link = h.view.querySelector('[data-request-permissions-result] a'); assert.equal(link.href, 'https://seerr.example/base'); assert.equal(link.rel, 'noopener noreferrer');
    assert.equal(h.calls.some(c => c.body), false);
  } finally { h.close(); }
});

test('native player destinations require explicit preview/confirmation and receipt refresh never repeats a command', async () => {
  const h = await harness();
  try {
    const item = '11111111-2222-3333-4444-555555555555', plan = 'a'.repeat(48);
    h.control.reply = async options => options.url.includes('/Catalog/Browse?') ? { Items: [{ ...mediaCard, Kind: 'movie', NativeId: item }], Page: 1, Pages: 1 } : options.url.endsWith('/movie/123/Details') ? { ...mediaCard, Kind: 'movie', NativeId: item } : options.url.endsWith('/Players') ? [{ Id: 'real-tv', Name: '<img src=x> TV', Client: 'Native' }] : options.url.endsWith('/Players/Prepare') ? { Id: plan, Title: 'Movie', Device: 'TV' } : options.url.endsWith('/Players/' + plan + '/Confirm') ? { Id: plan, State: 'uncertain', ObservedPlayingItem: false } : options.url.endsWith('/Players/' + plan + '/Receipt') ? { Id: plan, State: 'uncertain', ObservedPlayingItem: true } : undefined;
    const form = h.view.querySelector('[data-catalog-query]'); form.elements.source.value = 'browse'; submit(h, '[data-catalog-query]'); await h.settle(); h.view.querySelector('[data-catalog-list] button').click(); await h.settle();
    const panel = h.view.querySelector('[data-media-details]'); const click = text => [...panel.querySelectorAll('button')].find(b => b.textContent === text).click();
    click('Choose one of your real Jellyfin players'); await h.settle(); assert.equal(h.calls.some(c => c.url.endsWith('/Players/Prepare')), false);
    assert.equal(panel.querySelector('img'), null); click('Review play command for this player'); await h.settle(); assert.equal(h.calls.some(c => c.url.endsWith('/Confirm') && c.url.includes('/Players/')), false);
    click('Confirm native play command'); await h.settle(); assert.match(panel.textContent, /Acceptance is not playback confirmation/);
    click('Refresh player receipt · never resend'); await h.settle(); assert.equal(h.calls.filter(c => c.url.endsWith('/Players/' + plan + '/Confirm')).length, 1);
    assert.match(panel.textContent, /does not prove this command caused it/);
  } finally { h.close(); }
});
test('native library details do not depend on Seerr even when the native item has a TMDB ID', async () => {
  const h = await harness();
  try {
    const card = { ...mediaCard, Kind: 'movie', NativeId: '11111111-2222-3333-4444-555555555555', NativeOnly: true };
    h.control.reply = async options => options.url.includes('/Catalog/Library?') ? { Items: [card], Page: 1, Pages: 1 } : options.url.endsWith('/Items/' + card.NativeId + '/Details') ? card : undefined;
    submit(h, '[data-catalog-query]'); await h.settle(); h.view.querySelector('[data-catalog-list] button').click(); await h.settle();
    assert.ok(h.calls.some(c => c.url.endsWith('/Items/' + card.NativeId + '/Details')));
    assert.equal(h.calls.some(c => c.url.endsWith('/Catalog/movie/123/Details')), false);
    assert.match(h.view.querySelector('[data-media-details]').textContent, /Open in Jellyfin/);
  } finally { h.close(); }
});

test('uncertain watchlist review adopts observed membership only after explicit confirmation without replaying sync', async () => {
  const h = await harness();
  try {
    h.control.reply = async options => options.url.endsWith('/Sync/Issues') ? [{ Kind: 'movie', ExternalId: 123, Title: 'Saved title', State: 'uncertain', Desired: true }] : options.url.endsWith('/Sync/Review') ? { Token: 'opaque-proof', Title: 'Saved title', RemoteMembership: false } : options.url.endsWith('/Sync/AcceptRemote') ? { Complete: true } : undefined;
    h.view.querySelector('[data-sync-issues]').click(); await h.settle(); h.view.querySelector('[data-sync-review] button').click(); await h.settle();
    assert.equal(h.calls.some(c => c.url.endsWith('/Sync/AcceptRemote')), false); h.dom.window.confirm = () => false; h.view.querySelector('[data-sync-review] button').click(); await h.settle();
    assert.equal(h.calls.some(c => c.url.endsWith('/Sync/AcceptRemote')), false); h.dom.window.confirm = () => true; h.view.querySelector('[data-sync-review] button').click(); await h.settle();
    assert.equal(h.calls.find(c => c.url.endsWith('/Sync/AcceptRemote')).body.Token, 'opaque-proof'); assert.equal(h.calls.some(c => c.url.endsWith('/Watchlist/Sync')), false);
    assert.equal(h.dom.window.localStorage.length, 0);
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
    assert.match(h.view.querySelector('[data-settings-notice]').textContent, /unsigned settings require explicit review/);
    assert.equal(h.view.querySelector('[data-configuration]').elements.automaticDeletion.value, 'false');
    assert.match(h.view.querySelector('[data-settings-approvals] input[type="url"]').value, /arr.example/);
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
       assert.equal(h.subscriptions.length, 0);
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
    assert.deepEqual([...buttons].map(button => button.textContent), ['Yes', 'No', 'help_outline']);
    assert.equal(buttons[0].classList.contains('button-submit'), true);
    assert.equal(buttons[2].getAttribute('aria-label'), 'Help about deletion');
    const details = h.view.querySelector('#' + buttons[2].getAttribute('aria-controls')); assert.equal(details.hidden, true);
    buttons[2].click(); assert.equal(details.hidden, false); assert.match(details.textContent, /New activity invalidates consent/);
    assert.equal(h.calls.some(call => call.url.endsWith('/Me/Consent/notice')), false);
    buttons[1].click(); await h.settle();
    const write = h.calls.find(call => call.url.endsWith('/Me/Consent/notice'));
    assert.equal(write.body.Approve, false);
    assert.equal(h.calls.some(call => call.url.endsWith('/Activity')), false);
  } finally { h.close(); }
});
test('consent page polling preserves item-specific keyboard focus and expanded rules until visibility is lost', async () => {
  const h = await harness();
  try {
    let rows = [{ Id: 'one', Title: 'First movie' }, { Id: 'two', Title: 'Second movie' }];
    h.control.reply = async options => options.url.endsWith('/Me/Consent') ? rows : undefined;
    await poll(h);
    const help = h.view.querySelector('[data-consent-focus-key="two:help"]'); help.click(); help.focus();
    await poll(h);
    const restored = h.view.querySelector('[data-consent-focus-key="two:help"]');
    assert.equal(h.dom.window.document.activeElement, restored); assert.equal(restored.getAttribute('aria-expanded'), 'true');
    assert.equal(h.view.querySelector('#' + restored.getAttribute('aria-controls')).hidden, false);
    rows = [rows[0]]; await poll(h); assert.equal(h.view.querySelector('[data-consent-focus-key="two:help"]'), null);
    assert.equal(h.calls.some(call => call.url.endsWith('/Me/Consent/two')), false);
  } finally { h.close(); }
});
