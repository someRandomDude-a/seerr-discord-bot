import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import vm from 'node:vm';
import { JSDOM } from 'jsdom';

async function harness() {
    const html = await readFile(new URL('../../src/Jellyfin.Plugin.MediaHub.Discord/Web/settings.html', import.meta.url), 'utf8');
    const js = await readFile(new URL('../../src/Jellyfin.Plugin.MediaHub.Discord/Web/settings.js', import.meta.url), 'utf8');
    const dom = new JSDOM(html, { url: 'https://jellyfin.example/web/' }); dom.window.confirm = () => true;
    const view = dom.window.document.querySelector('#mediaHubDiscordPage'), calls = [], data = { Revision: 0, MigrationReviewRequired: true, DiscordEnabled: false, AllowedGuilds: ['555'], ApplicationId: '123456', ActivityEnabled: false, OAuthPkce: true, MessageContent: false, MembersIntent: false, Operators: ['42'], SeerrOperators: false, InboxChannels: [], MessageRetentionDays: 7, MaxRecipients: 10 };
    let account = 'native-admin', reply = async body => body ? { ...data, ...body, Revision: 1, MigrationReviewRequired: false } : data;
    const api = async body => { calls.push(body ? JSON.parse(JSON.stringify(body)) : null); return reply(body); };
    vm.runInContext(js, vm.createContext({ window: dom.window, document: dom.window.document }));
    const ui = dom.window.JellyfinDiscordSettings(view, api, () => account), settle = () => new Promise(resolve => setImmediate(resolve)); ui.show(); await settle();
    return { dom, view, calls, ui, settle, form: view.querySelector('form'), reply(fn) { reply = fn; }, account(id) { account = id; }, close() { ui.hide(); dom.window.close(); } };
}
test('adapter configuration imports only on explicit confirmed save and never persists browser secrets', async () => {
    const h = await harness();
    try {
        assert.equal(h.calls.filter(Boolean).length, 0); assert.match(h.view.textContent, /explicitly imports/);
        h.form.elements.BotToken.value = 'replacement-test-secret';
        h.dom.window.confirm = () => false; h.form.dispatchEvent(new h.dom.window.Event('submit', { cancelable: true })); await h.settle(); assert.equal(h.calls.filter(Boolean).length, 0);
        h.dom.window.confirm = () => true; h.form.dispatchEvent(new h.dom.window.Event('submit', { cancelable: true })); await h.settle();
        const save = h.calls.find(Boolean); assert.equal(save.ImportLegacy, true); assert.equal(save.Revision, 0); assert.equal(save.BotToken, 'replacement-test-secret');
        assert.equal(h.form.elements.BotToken.value, ''); assert.equal(h.form.elements.ClientSecret.value, '');
        assert.equal(h.dom.window.localStorage?.length || 0, 0);
    } finally { h.close(); }
});
test('uncertain saves block resubmission and late account-switch responses cannot repopulate fields', async () => {
    const h = await harness();
    try {
        h.reply(async () => { throw new Error('Write not confirmed'); }); h.form.dispatchEvent(new h.dom.window.Event('submit', { cancelable: true })); await h.settle();
        h.form.dispatchEvent(new h.dom.window.Event('submit', { cancelable: true })); await h.settle(); assert.equal(h.calls.filter(Boolean).length, 1);
        let finish; h.reply(async () => new Promise(resolve => { finish = resolve; })); h.view.querySelector('[data-discord-reload]').click();
        h.account('another-admin'); finish({ Revision: 99, AllowedGuilds: ['999'] }); await h.settle();
        assert.equal(h.view.querySelector('[data-discord-fields]').hidden, true); assert.equal(h.form.elements.BotToken.value, '');
    } finally { h.close(); }
});
