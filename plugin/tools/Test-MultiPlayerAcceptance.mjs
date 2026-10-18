import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
let stage = 'fixture verification', revoked = false;
process.on('uncaughtException', () => { console.error('Owned multi-player test failed during ' + stage + '. Private logs suppressed.'); process.exitCode = 1; });
process.on('unhandledRejection', () => { console.error('Owned multi-player test failed during ' + stage + '. Private logs suppressed.'); process.exitCode = 1; });
const [stateFile, playwrightModule] = process.argv.slice(2), state = JSON.parse(await readFile(stateFile, 'utf8'));
if (state.Name !== 'mediahub-test-visibility' || state.Port !== 18496 || !state.LibraryAcceptance?.IntentionalFixtureSetup) throw new Error('Only the owned generated-media fixture is allowed.');
const origin = 'http://127.0.0.1:18496', user = state.LibraryAcceptance.Viewer, item = state.LibraryAcceptance.Visible;
const { chromium } = await import(pathToFileURL(resolve(playwrightModule)).href);
const browser = await chromium.launch({ ...(process.env.MEDIAHUB_BROWSER_CHANNEL === 'chromium' ? {} : { channel: 'msedge' }), headless: true });
async function login(name, password) {
    const page = await (await browser.newContext()).newPage(); await page.goto(origin + '/web/'); await page.locator('#txtManualName').waitFor({ state: 'attached' });
    if (!await page.locator('#txtManualName').isVisible()) { try { await page.locator('.btnManual').click({ timeout: 5000 }); } catch { if (!await page.locator('#txtManualName').isVisible()) throw new Error('Native manual login unavailable'); } }
    await page.locator('#txtManualName').waitFor({ state: 'visible' }); await page.locator('#txtManualName').fill(name); await page.locator('#txtManualPassword').fill(password);
    await page.locator('.manualLoginForm button[type="submit"]').click(); await page.waitForFunction(() => !!window.ApiClient?.getCurrentUserId?.() && /#\/home/.test(location.hash)); return page;
}
async function api(page, path, body) {
    return page.evaluate(async ({ path, body }) => {
        try { return { Status: 200, Data: await window.ApiClient.ajax({ type: body === undefined ? 'GET' : 'POST', url: window.ApiClient.getUrl(path), dataType: 'json', contentType: 'application/json', data: body === undefined ? undefined : JSON.stringify(body) }) }; }
        catch (error) { return { Status: error.status || 0 }; }
    }, { path, body });
}
async function admin(path, body) {
    const response = await fetch(origin + '/' + path, { method: body === undefined ? 'GET' : 'POST', headers: { Authorization: 'MediaBrowser Client="Media Hub acceptance", Device="Disposable test", DeviceId="mediahub-acceptance-admin", Version="0.1", Token="' + state.AdminToken + '"', 'Content-Type': 'application/json' }, body: body === undefined ? undefined : JSON.stringify(body), signal: AbortSignal.timeout(15000) });
    if (!response.ok) throw new Error('Owned native policy operation failed'); return response.status === 204 ? null : response.json();
}
async function ownSession(page) {
    return page.evaluate(async () => {
        const device = window.ApiClient.deviceId();
        const sessions = await window.ApiClient.ajax({ type: 'GET', url: window.ApiClient.getUrl('Sessions', { DeviceId: device }), dataType: 'json' });
        const own = sessions.filter(row => row.DeviceId === device); if (own.length !== 1) throw new Error('Native browser session ambiguous'); return own[0].Id;
    });
}
const playing = () => [...document.querySelectorAll('video')].some(video => !video.paused && video.currentTime > .25 && video.readyState >= 2 && video.videoWidth > 0);
let originalPlayback;
try {
    stage = 'two independent authenticated native browsers'; const first = await login('mediahub-viewer', state.ViewerPassword), second = await login('mediahub-viewer', state.ViewerPassword);
    await first.waitForFunction(async () => (await window.ApiClient.ajax({ type: 'GET', url: window.ApiClient.getUrl('MediaHub/Players'), dataType: 'json' })).length === 2);
    const firstId = await ownSession(first), secondId = await ownSession(second); assert.notEqual(firstId, secondId);
    stage = 'administrator cannot target another account’s player'; const other = await login('mediahub-admin', state.AdminPassword);
    const excluded = await api(other, 'MediaHub/Players'); assert.equal(excluded.Status, 200); assert.ok(excluded.Data.every(row => row.Id !== firstId && row.Id !== secondId));
    assert.equal((await api(other, 'MediaHub/Players/Prepare', { Target: secondId, Item: item })).Status, 400);
    stage = 'one explicit command to the other real same-account player';
    const preview = await api(first, 'MediaHub/Players/Prepare', { Target: secondId, Item: item }); assert.equal(preview.Status, 200);
    const accepted = await api(first, 'MediaHub/Players/' + preview.Data.Id + '/Confirm', {}); assert.equal(accepted.Status, 200); assert.equal(accepted.Data.State, 'accepted');
    await second.waitForFunction(playing, undefined, { timeout: 20000 }); assert.equal(await first.evaluate(playing), false);
    await second.evaluate(id => window.ApiClient.ajax({ type: 'POST', url: window.ApiClient.getUrl('Sessions/' + id + '/Playing/Stop') }), secondId);
    stage = 'cancellation never sends the preview';
    const cancelled = await api(second, 'MediaHub/Players/Prepare', { Target: firstId, Item: item }); assert.equal(cancelled.Status, 200);
    assert.equal((await api(second, 'MediaHub/Players/' + cancelled.Data.Id + '/Cancel', {})).Data.State, 'cancelled');
    assert.equal((await api(second, 'MediaHub/Players/' + cancelled.Data.Id + '/Confirm', {})).Data.State, 'cancelled'); assert.equal(await first.evaluate(playing), false);
    stage = 'live permission loss between preview and confirmation';
    const denied = await api(second, 'MediaHub/Players/Prepare', { Target: firstId, Item: item }); assert.equal(denied.Status, 200);
    const policy = (await admin('Users/' + user)).Policy; originalPlayback = policy.EnableMediaPlayback; assert.equal(originalPlayback, true); policy.EnableMediaPlayback = false;
    await admin('Users/' + user + '/Policy', policy); revoked = true;
    assert.equal((await api(second, 'MediaHub/Players/' + denied.Data.Id + '/Confirm', {})).Status, 400); assert.equal(await first.evaluate(playing), false);
    const current = (await admin('Users/' + user)).Policy; current.EnableMediaPlayback = originalPlayback; await admin('Users/' + user + '/Policy', current); revoked = false;
    assert.equal((await api(second, 'MediaHub/Players/' + denied.Data.Id + '/Cancel', {})).Data.State, 'cancelled');
    console.log(JSON.stringify({ Fixture: state.Name, TwoRealSameAccountPlayers: true, ExactRemotePlayerDecodedVideo: true, AdministratorOwnershipBypassRejected: true, CancelledPreviewNeverPlayed: true, LivePermissionLossRejected: true, NativePolicyRestored: true, ProviderPartyConcurrencyTested: false, CredentialsLogged: false }));
} catch { console.error('Owned multi-player acceptance failed during ' + stage + '. No uncertain command was retried. Private logs suppressed.'); process.exitCode = 1; }
finally {
    if (revoked) { try { const policy = (await admin('Users/' + user)).Policy; policy.EnableMediaPlayback = originalPlayback; await admin('Users/' + user + '/Policy', policy); } catch { console.error('Disposable viewer policy restoration requires review. No reset was attempted.'); process.exitCode = 1; } }
    await browser.close();
}
