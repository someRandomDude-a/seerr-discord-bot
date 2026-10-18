// Optional developer acceptance tool. Uses an existing test-only browser package, never a plugin runtime dependency.
import assert from 'node:assert/strict';
import { readFile, mkdir } from 'node:fs/promises';
import { resolve, dirname } from 'node:path';
import { pathToFileURL } from 'node:url';
// Browser errors can include filled arguments. Never print fixture credentials in failure call logs.
let stage = 'fixture loading';
process.on('uncaughtException', () => { console.error('Native UI acceptance failed during ' + stage + '. Credentials and browser call logs suppressed.'); process.exitCode = 1; });
process.on('unhandledRejection', () => { console.error('Native UI acceptance failed during ' + stage + '. Credentials and browser call logs suppressed.'); process.exitCode = 1; });

const [stateFile, playwrightModule, screenshot, axeFile] = process.argv.slice(2);
if (!stateFile || !playwrightModule || !screenshot) throw new Error('Provide owned fixture state, playwright-core module and screenshot path.');
const state = JSON.parse(await readFile(stateFile, 'utf8'));
if (!(state.Name === 'mediahub-test-settings' && state.Port === 18296 && !state.BasePath || state.Name === 'mediahub-test-completion' && state.Port === 18396 && state.BasePath === '/mediahub' || state.Name === 'mediahub-test-split' && state.Port === 18596 && !state.BasePath || state.Name === 'mediahub-test-release-diag' && state.Port === 18896 && !state.BasePath)) throw new Error('Only specifically owned native UI fixtures are authorized by this tool.');
const basePath = state.BasePath || '', origin = 'http://127.0.0.1:' + state.Port, server = origin + basePath;
const { chromium } = await import(pathToFileURL(resolve(playwrightModule)).href);
stage = 'browser launch';
const browser = await chromium.launch({ ...(process.env.MEDIAHUB_BROWSER_CHANNEL === 'chromium' ? {} : { channel: 'msedge' }), headless: true });
let page;
const accessibility = [];
async function audit(selector, label) {
  if (!axeFile) return;
  await page.evaluate(await readFile(axeFile, 'utf8'));
  const result = await page.evaluate(async selector => {
      const results = await window.axe.run(document.querySelector(selector), { runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21aa'] } });
      return { Version: window.axe.version, Violations: results.violations.map(row => ({ Id: row.id, Impact: row.impact, Nodes: row.nodes.length })) };
  }, selector);
  accessibility.push({ Surface: label, ...result }); assert.deepEqual(result.Violations, [], 'Scoped WCAG automated checks failed: ' + label);
}
const loginStatuses = [];
const settingsStatuses = [];
try {
  const context = await browser.newContext({ viewport: { width: 1280, height: 960 } }); page = await context.newPage();
  const authentication = response => /\/users\/authenticatebyname$/i.test(new URL(response.url()).pathname);
  page.on('response', response => { if (authentication(response)) loginStatuses.push(response.status()); });
  page.on('response', response => { if (/\/(web\/configurationpage|mediahub\/(settings|status))$/i.test(new URL(response.url()).pathname)) settingsStatuses.push({ Resource: /configurationpage/i.test(new URL(response.url()).pathname) ? 'PluginResource' : /settings$/i.test(new URL(response.url()).pathname) ? 'Settings' : 'Status', Status: response.status() }); });
  let saves = 0;
  page.on('request', request => { if (new URL(request.url()).pathname.endsWith('/MediaHub/Settings') && request.method() === 'POST') saves++; });
  stage = 'native login'; await page.goto(server + '/web/');
  stage = 'login form attachment';
  await page.locator('#txtManualName').waitFor({ state: 'attached' });
  stage = 'manual login selection';
  if (!await page.locator('#txtManualName').isVisible()) {
    try { await page.locator('.btnManual').click({ timeout: 5000 }); }
    catch { if (!await page.locator('#txtManualName').isVisible()) throw new Error('Native manual login did not become available.'); }
  }
  await page.locator('#txtManualName').waitFor({ state: 'visible' });
  stage = 'login credentials entry';
  await page.locator('#txtManualName').fill('mediahub-admin'); await page.locator('#txtManualPassword').fill(state.AdminPassword);
  stage = 'login submission';
  const authenticated = page.waitForResponse(authentication);
  await page.locator('.manualLoginForm button[type="submit"]').click();
  assert.equal((await authenticated).status(), 200, 'Fixture browser authentication must succeed');
  stage = 'authenticated browser readiness';
  await page.waitForFunction(() => {
    const route = window.location.hash.slice(1).replace(/^!?\//, '').split('?')[0].replace(/\.html$/, '');
    return !!window.ApiClient?.getCurrentUserId?.() && !!window.ApiClient?.accessToken?.() && (route === 'home' || /\/home(?:\.html)?$/.test(window.location.pathname));
  });
  stage = 'settings loading'; await page.evaluate(() => { window.Dashboard.navigate('configurationpage?name=mediahub'); });
  const section = page.locator('[data-settings]'); await section.waitFor({ state: 'visible' });
  stage = 'administrator deletion monitoring';
  await page.locator('[data-monitoring]').waitFor({ state: 'visible' });
  await page.getByText('No proposals match these filters.', { exact: true }).waitFor({ state: 'visible' });
  assert.equal(await page.locator('[data-monitor-list] button').count(), 0);
  assert.equal(await page.locator('[data-tutorial-start],[data-onboarding]').count(), 0);
  const selector = page.locator('[data-settings-section]'); await selector.selectOption('discord');
  stage = 'section draft retention';
  const token = page.locator('#hubSetting-discordToken'); await token.fill('non-secret-browser-draft');
  await selector.selectOption('activity'); await selector.selectOption('discord'); assert.equal(await token.inputValue(), 'non-secret-browser-draft');
  const help = page.getByRole('button', { name: 'Help for Discord bot token', exact: true }); await help.click();
  stage = 'native generated label and help';
  assert.equal(await help.getAttribute('aria-expanded'), 'true');
  assert.ok(await page.locator('label[for="hubSetting-discordToken"]').count(), 'Native Jellyfin component must generate its label');
  await token.fill(''); await selector.selectOption('general');
  stage = 'automated native settings accessibility'; await audit('#mediaHubPage', 'settings');
  await mkdir(dirname(resolve(screenshot)), { recursive: true }); await page.screenshot({ path: resolve(screenshot), fullPage: true });
  stage = 'narrow-screen settings and keyboard focus';
  await page.setViewportSize({ width: 390, height: 844 });
  await selector.focus(); assert.equal(await selector.evaluate(element => document.activeElement === element), true);
  const narrow = await selector.boundingBox(); assert.ok(narrow && narrow.x >= 0 && narrow.x + narrow.width <= 391, 'Settings selector must fit the narrow viewport');
  await page.screenshot({ path: resolve(screenshot).replace(/\.png$/i, '-mobile.png'), fullPage: true });
  stage = 'native navigation clears private drafts';
  await selector.selectOption('discord'); await token.fill('non-secret-navigation-draft');
  await page.evaluate(() => { window.Dashboard.navigate('home'); });
   await page.waitForFunction(() => !document.querySelector('#hubSetting-discordToken')?.value && (!document.querySelector('[data-settings]') || document.querySelector('[data-settings]').hidden));
   stage = 'native top bar and notification dialog';
   // Required upstream plugins inject the installed resource. Never add a script manually.
   assert.equal(await page.locator('script[data-mediahub-bootstrap]').count(), 1);
   const bell = page.locator('[data-notification-bell]'); await bell.waitFor({ state: 'visible' });
   assert.ok(await bell.locator('xpath=ancestor::*[contains(@class,"MuiToolbar-root")]').count(), 'Use Jellyfin 12’s visible native header, never its hidden legacy header');
   const bellBounds = await bell.boundingBox(); assert.ok(bellBounds && bellBounds.x >= 0 && bellBounds.x + bellBounds.width <= 391, 'Top bell must fit mobile viewport');
   const watch = page.getByRole('link', { name: 'Your watchlist', exact: true }); assert.match(await watch.getAttribute('href'), /^#\/userpluginsettings\.html\?/);
   await bell.click(); const notifications = page.getByRole('dialog', { name: 'Notifications', exact: true }); await notifications.waitFor({ state: 'visible' });
   assert.equal(await notifications.evaluate(node => {
       const color = value => value.match(/[\d.]+/g)?.map(Number);
       const foreground = color(getComputedStyle(node).color), background = color(getComputedStyle(node).backgroundColor);
       const alpha = foreground?.[3] ?? 1, text = foreground?.slice(0, 3).map((value, i) => value * alpha + background[i] * (1 - alpha));
       const luminance = values => values.map(value => value / 255).map(value => value <= .04045 ? value / 12.92 : ((value + .055) / 1.055) ** 2.4).reduce((sum, value, i) => sum + value * [.2126, .7152, .0722][i], 0);
       return text && background && (Math.max(luminance(text), luminance(background.slice(0, 3))) + .05) / (Math.min(luminance(text), luminance(background.slice(0, 3))) + .05) >= 4.5;
   }), true, 'Native theme must provide readable notification dialog foreground/background');
   await page.screenshot({ path: resolve(screenshot).replace(/\.png$/i, '-notifications-mobile.png'), fullPage: true });
   await page.keyboard.press('Escape'); await page.locator('[data-mediahub-menu]').waitFor({ state: 'detached' });
   assert.equal(await bell.evaluate(node => document.activeElement === node), true, 'Closing notifications restores keyboard focus');
   stage = 'dedicated watchlist native page';
   await watch.click();
   await page.getByRole('heading', { name: 'Your watchlist', exact: true, level: 1 }).waitFor({ state: 'visible' });
   stage = 'watchlist section isolation';
   assert.equal(await page.locator('[data-watchlist-page]').isVisible(), true); assert.equal(await page.locator('[data-settings]').isVisible(), false); assert.equal(await page.locator('[data-preferences]').isVisible(), false);
   stage = 'watchlist notification glyph';
   await page.waitForFunction(() => document.querySelectorAll('[data-notification-bell]').length === 1 && document.querySelector('[data-notification-bell] .material-icons')?.textContent.startsWith('notifications_'));
   await bell.waitFor({ state: 'visible' });
   stage = 'watchlist top bar bounds';
   const watchlistBell = await bell.boundingBox(); assert.ok(watchlistBell && watchlistBell.x >= 0 && watchlistBell.x + watchlistBell.width <= 391, 'Bell remains visible after native page/header replacement');
   await page.screenshot({ path: resolve(screenshot).replace(/\.png$/i, '-watchlist-mobile.png'), fullPage: true });
   assert.equal(saves, 0); await context.close();
   stage = 'ordinary viewer native login';
   const viewerContext = await browser.newContext({ viewport: { width: 390, height: 844 } }); page = await viewerContext.newPage();
   let viewerWrites = 0; page.on('request', request => { if (new URL(request.url()).pathname.startsWith(basePath + '/MediaHub/') && request.method() !== 'GET') viewerWrites++; });
   await page.goto(server + '/web/'); await page.locator('#txtManualName').waitFor({ state: 'attached' });
   if (!await page.locator('#txtManualName').isVisible()) { try { await page.locator('.btnManual').click({ timeout: 5000 }); } catch { if (!await page.locator('#txtManualName').isVisible()) throw new Error('Manual login unavailable'); } }
   await page.locator('#txtManualName').waitFor({ state: 'visible' }); await page.locator('#txtManualName').fill('mediahub-viewer'); await page.locator('#txtManualPassword').fill(state.ViewerPassword);
   await page.locator('.manualLoginForm button[type="submit"]').click(); await page.waitForFunction(() => !!window.ApiClient?.getCurrentUserId?.() && (/#\/home/.test(location.hash) || /\/home$/.test(location.pathname)));
   stage = 'ordinary viewer dependency registrations and automatic injection';
   await page.locator('[data-notification-bell]').waitFor({ state: 'visible' }); assert.equal(await page.locator('script[data-mediahub-bootstrap]').count(), 1);
   const web = await page.evaluate(async () => (await window.ApiClient.ajax({ type: 'GET', url: window.ApiClient.getUrl('MediaHub/Status'), dataType: 'json' })).Web);
   assert.equal(web.Required, true); assert.equal(web.RegistrationsReady, true); assert.equal(web.Dependencies.length, 2);
   stage = 'ordinary viewer Plugin Pages watchlist'; await page.getByRole('link', { name: 'Your watchlist', exact: true }).click();
   await page.getByRole('heading', { name: 'Your watchlist', exact: true, level: 1 }).waitFor({ state: 'visible' }); assert.equal(await page.locator('[data-settings]').isVisible(), false);
   stage = 'ordinary viewer Plugin Pages notifications'; await page.locator('[data-notification-bell]').click(); await page.getByRole('link', { name: 'All notifications', exact: true }).click();
   await page.getByRole('heading', { name: 'Notifications', exact: true, level: 1 }).waitFor({ state: 'visible' });
   stage = 'authenticated notification deep-link reload'; await page.reload();
   await page.getByRole('heading', { name: 'Notifications', exact: true, level: 1 }).waitFor({ state: 'visible' });
   await page.locator('[data-notification-bell]').waitFor({ state: 'visible' });
   assert.equal(await page.locator('[data-notification-bell]').count(), 1); assert.equal(await page.locator('script[data-mediahub-bootstrap]').count(), 1);
   stage = 'automated notification page accessibility'; await audit('#mediaHubPage', 'notifications');
   assert.equal(viewerWrites, 0);
   stage = 'mock consent visual preview (no vote or real media)';
   // Presentation-only responses. All mutations are blocked; never create real proposals.
   await viewerContext.route('**/MediaHub/**', async route => {
       const request = route.request(), path = new URL(request.url()).pathname;
       if (request.method() !== 'GET') { await route.abort(); return; }
       const rows = path.endsWith('/Me/Consent') ? [{ Id: 'preview', Proposal: 'preview', Title: 'Preview only · Example movie' }] : path.endsWith('/Deletions') ? [{ Id: 'preview', Title: 'Preview only · Example movie', State: 'waiting', AutomaticDeletion: false }] : null;
       if (rows) await route.fulfill({ contentType: 'application/json', body: JSON.stringify(rows) }); else await route.continue();
   });
   await page.locator('[data-notification-bell]').click(); const preview = page.getByRole('dialog', { name: 'Notifications', exact: true }); await preview.waitFor({ state: 'visible' });
   await preview.getByRole('button', { name: 'Yes', exact: true }).waitFor({ state: 'visible' }); assert.equal(await preview.getByText('Allow deletion?', { exact: true }).count(), 1);
   const deletionHelp = preview.getByRole('button', { name: 'Help about deletion', exact: true }); assert.equal(await deletionHelp.getAttribute('aria-expanded'), 'false');
   stage = 'keyboard consent and modal focus containment';
   await preview.getByRole('button', { name: 'Yes', exact: true }).focus(); await page.keyboard.press('Tab');
   assert.equal(await preview.getByRole('button', { name: 'No', exact: true }).evaluate(node => document.activeElement === node), true);
   await page.keyboard.press('Tab'); assert.equal(await deletionHelp.evaluate(node => document.activeElement === node), true);
   stage = 'automated compact consent accessibility'; await audit('[data-mediahub-menu]', 'compact consent');
   await page.screenshot({ path: resolve(screenshot).replace(/\.png$/i, '-consent-preview.png'), fullPage: true });
   await deletionHelp.press('Enter'); assert.equal(await deletionHelp.getAttribute('aria-expanded'), 'true'); await preview.getByText('Opening notifications or Help does not vote, mark updates read, or record media activity.', { exact: true }).waitFor({ state: 'visible' });
   stage = 'automated expanded consent accessibility'; await audit('[data-mediahub-menu]', 'expanded consent');
   stage = 'expanded modal keyboard containment';
   for (let index = 0; index < 8; index++) { await page.keyboard.press('Tab'); assert.equal(await preview.evaluate(node => node.contains(document.activeElement)), true, 'Modal keyboard focus must not escape to the background'); }
   await page.screenshot({ path: resolve(screenshot).replace(/\.png$/i, '-consent-help-preview.png'), fullPage: true });
   assert.equal(viewerWrites, 0); await viewerContext.close();
    console.log(JSON.stringify({ Fixture: state.Name, BasePath: basePath, NativeSettingsVisible: true, AdministratorEmptyBacklogVisible: true, NativeGeneratedLabels: true, SectionDraftPreserved: true, ContextualHelp: true, NarrowSettingsSelectorFits: true, NativeKeyboardFocus: true, ModalKeyboardContainment: true, NotificationDeepLinkReload: true, Accessibility: accessibility, NavigationClearsPrivateDrafts: true, VisibleNativeTopBell: true, MobileNotificationDialog: true, DedicatedWatchlistPage: true, OrdinaryViewerPages: true, DependencyRegistrationsReady: true, CompactConsentPreviewUsesMockData: true, ItemResourceLoadedExplicitly: false, ImplicitSettingsWrites: saves, ImplicitViewerWrites: viewerWrites, CredentialsLogged: false }));
} catch {
  if (page && !page.isClosed()) {
    const selectors = {};
    for (const selector of ['#txtManualName', '#txtManualPassword', '.btnManual', '.manualLoginForm button[type="submit"]', '[data-settings]']) {
      const element = page.locator(selector); selectors[selector] = { Attached: await element.count(), Visible: await element.first().isVisible().catch(() => false) };
    }
    const readiness = await page.evaluate(() => ({ Client: !!window.ApiClient, CurrentUser: !!window.ApiClient?.getCurrentUserId?.(), TokenPresent: !!window.ApiClient?.accessToken?.(), LoginRoute: /\/login(?:\.html)?$/.test(window.location.pathname), HomeRoute: /\/home(?:\.html)?$/.test(window.location.pathname), ConfigurationRoute: /\/configurationpage$/.test(window.location.pathname), LegacyLoginHash: /^#!?\/login(?:[.?]|$)/.test(window.location.hash), Mounted: document.querySelector('#mediaHubPage')?.dataset.hubMounted, FieldCount: document.querySelectorAll('[data-settings-fields] input').length, Initializers: ['JellyfinMediaHub', 'JellyfinMediaSettings', 'JellyfinMediaMessenger', 'JellyfinMediaWatchParty'].map(name => typeof window[name]), LoadErrorVisible: document.querySelector('[data-notice]')?.textContent === 'Unable to load Media Hub. Refresh the page.' })).catch(() => ({}));
    const topBar = await page.locator('[data-notification-bell]').evaluateAll(nodes => nodes.map(node => { const box = node.getBoundingClientRect(); return { Classes: node.className, Icon: node.querySelector('.material-icons')?.textContent, X: box.x, Y: box.y, Width: box.width, Height: box.height, ParentClasses: node.parentElement?.parentElement?.className }; })).catch(() => []);
    const bootstrap = await page.evaluate(() => ({ Scripts: document.querySelectorAll('script[data-mediahub-bootstrap]').length, ControlsInitialized: !!window.mediaHubItemControls })).catch(() => ({}));
    console.error(JSON.stringify({ Stage: stage, Accessibility: accessibility, Selectors: selectors, Readiness: readiness, Bootstrap: bootstrap, TopBar: topBar, LoginHttpStatuses: loginStatuses, SettingsHttpStatuses: settingsStatuses, CredentialsLogged: false }));
  }
  throw new Error('Native UI acceptance failed; private call logs suppressed.');
} finally { await browser.close(); }
