// Real generated-media playback in one owned disposable native browser, never a synthetic session.
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
const [stateFile, playwrightModule] = process.argv.slice(2);
let stage = 'fixture verification';
process.on('uncaughtException', () => { console.error('Owned player test failed during ' + stage + '. Private call logs suppressed.'); process.exitCode = 1; });
process.on('unhandledRejection', () => { console.error('Owned player test failed during ' + stage + '. Private call logs suppressed.'); process.exitCode = 1; });
const state = JSON.parse(await readFile(stateFile, 'utf8'));
if (state.Name !== 'mediahub-test-visibility' || state.Port !== 18496 || !state.LibraryAcceptance?.IntentionalFixtureSetup) throw new Error('Only the owned, generated-media visibility fixture is allowed.');
const { chromium } = await import(pathToFileURL(resolve(playwrightModule)).href);
const browser = await chromium.launch({ ...(process.env.MEDIAHUB_BROWSER_CHANNEL === 'chromium' ? {} : { channel: 'msedge' }), headless: true });
try {
    const page = await browser.newPage();
    stage = 'native viewer login'; await page.goto('http://127.0.0.1:18496/web/'); await page.locator('#txtManualName').waitFor({ state: 'attached' });
    if (!await page.locator('#txtManualName').isVisible()) { try { await page.locator('.btnManual').click({ timeout: 5000 }); } catch { if (!await page.locator('#txtManualName').isVisible()) throw new Error('Manual login unavailable'); } }
    await page.locator('#txtManualName').waitFor({ state: 'visible' }); await page.locator('#txtManualName').fill('mediahub-viewer'); await page.locator('#txtManualPassword').fill(state.ViewerPassword);
    await page.locator('.manualLoginForm button[type="submit"]').click(); await page.waitForFunction(() => !!window.ApiClient?.getCurrentUserId?.() && /#\/home/.test(location.hash));
    stage = 'native media detail controls'; await page.evaluate(id => { location.hash = '#/details?id=' + id + '&serverId=' + window.ApiClient.serverId(); }, state.LibraryAcceptance.Visible);
    await page.locator('.itemDetailPage [data-mediahub-controls]').waitFor({ state: 'visible' });
    stage = 'real browser player capability discovery';
    await page.waitForFunction(async () => {
        const players = await window.ApiClient.ajax({ type: 'GET', url: window.ApiClient.getUrl('MediaHub/Players'), dataType: 'json' });
        return players.length === 1;
    });
    const players = await page.evaluate(() => window.ApiClient.ajax({ type: 'GET', url: window.ApiClient.getUrl('MediaHub/Players'), dataType: 'json' }));
    assert.equal(players.length, 1);
    stage = 'explicit once-only player preview and confirmation';
    const receipt = await page.evaluate(async ({ target, item }) => {
        const api = (path, body) => window.ApiClient.ajax({ type: 'POST', url: window.ApiClient.getUrl('MediaHub/Players/' + path), contentType: 'application/json', dataType: 'json', data: JSON.stringify(body) });
        const preview = await api('Prepare', { Target: target, Item: item });
        return api(preview.Id + '/Confirm', {}); // One confirmation only; never replay on an uncertain result.
    }, { target: players[0].Id, item: state.LibraryAcceptance.Visible });
    assert.equal(receipt.State, 'accepted');
    stage = 'observing actual decoded native video, not HTTP acceptance';
    await page.waitForFunction(() => [...document.querySelectorAll('video')].some(video => !video.paused && video.currentTime > 0.25 && video.readyState >= 2 && video.videoWidth > 0), undefined, { timeout: 20000 });
    stage = 'stopping only the verified disposable player';
    await page.evaluate(id => window.ApiClient.ajax({ type: 'POST', url: window.ApiClient.getUrl('Sessions/' + id + '/Playing/Stop') }), players[0].Id);
    console.log(JSON.stringify({ Fixture: state.Name, ActualNativeDetailControls: true, ActualAuthenticatedBrowserPlayer: true, SingleExplicitCommand: true, ReceiptState: receipt.State, DecodedAdvancingVideoObserved: true, GeneratedMediaOnly: true, MultiplePlayerConcurrencyTested: false, CredentialsLogged: false }));
} catch {
    console.error('Owned real-player acceptance failed during ' + stage + '. No uncertain command was retried. Private call logs suppressed.'); process.exitCode = 1;
} finally { await browser.close(); }
