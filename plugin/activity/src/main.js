import { DiscordSDK } from '@discord/embedded-app-sdk';

const root = document.querySelector('#app');
let session, profile, sdk, activeView, lifecycle = 0, ended = false;
// Direct .../Activity/App and Discord's directory-mapped / use the same relative transport.
const path = suffix => new URL(suffix, new URL('./', window.location.href)).href;
function end(message) {
  if (ended) return;
  ended = true; lifecycle++;
  activeView?.dispatchEvent(new Event('viewhide')); activeView = undefined;
  const token = session; session = undefined; profile = undefined;
  delete window.MediaHubNativeUrl; delete window.MediaHubOpenNative;
  if (token) void fetch(path('Logout'), { method: 'POST', headers: { 'X-MediaHub-Session': token }, credentials: 'omit', redirect: 'error', cache: 'no-store', keepalive: true }).catch(() => {});
  text(message);
}
async function call(url, body, raw = false, method) {
  if (ended || new URL(url).origin !== window.location.origin) throw new Error('Reopen Activity.');
  const run = lifecycle;
  const response = await fetch(url, { method: method || (body ? 'POST' : 'GET'), headers: { ...(body && !(body instanceof FormData) ? { 'Content-Type': 'application/json' } : {}), ...(session ? { 'X-MediaHub-Session': session } : {}) },
    body: body ? body instanceof FormData ? body : JSON.stringify(body) : undefined, credentials: 'omit', redirect: 'error', cache: 'no-store' });
  if (run !== lifecycle) throw new Error('Activity closed.');
  if (!response.ok) { const error = await response.json().catch(() => ({})); if (response.status === 401 && session) end('Verification expired. Reopen Activity.'); throw Object.assign(new Error(error.Error || 'Verification unavailable. Reopen Activity.'), { status: response.status, responseJSON: error }); }
  const result = await (raw ? response.blob() : response.json()); if (run !== lifecycle) throw new Error('Activity closed.'); return result;
}
function text(message) { root.replaceChildren(); const p = document.createElement('p'); p.textContent = message; root.append(p); }
function safeNative(url) { const parsed = new URL(url); if (parsed.protocol !== 'https:' || parsed.username || parsed.password) throw new Error('Invalid native destination.'); return parsed; }
async function linked() {
  profile = await call(path('Profile'));
  const themeKeys = { Background: '--hub-background', Text: '--hub-text', Accent: '--hub-accent', Surface: '--hub-surface' };
  for (const [key, property] of Object.entries(themeKeys)) { document.documentElement.style.removeProperty(property); if (/^#[0-9a-f]{6}$/i.test(profile.Theme?.[key] || '')) document.documentElement.style.setProperty(property, profile.Theme[key]); }
  if (profile.Linked) return true;
  text('Link your Jellyfin account in its own signed-in browser.');
  const link = document.createElement('button'); link.type = 'button'; link.textContent = 'Link Jellyfin';
  link.addEventListener('click', async () => {
    link.disabled = true;
    try { const result = await call(path('Link'), {}); const url = safeNative(result.Url); await sdk.commands.openExternalLink({ url: url.href }); }
    catch (e) { text(e.message); } finally { link.disabled = false; }
  });
  const refresh = document.createElement('button'); refresh.type = 'button'; refresh.textContent = 'Check link'; refresh.addEventListener('click', () => void startView().catch(e => text(e.message)));
  root.append(link, refresh); return false;
}
async function startView() {
  if (activeView || ended) return;
  if (!await linked()) return;
  const run = lifecycle;
  const response = await fetch(path('Shell'), { credentials: 'omit', redirect: 'error', cache: 'no-store' }); if (!response.ok) throw new Error('Activity shell unavailable.');
  const parsed = new DOMParser().parseFromString(await response.text(), 'text/html'); const view = parsed.querySelector('#mediaHubPage');
  view.querySelectorAll('script').forEach(s => s.remove()); // Fixed embedded template only; never render server/user metadata as HTML.
  for (const selector of ['[data-link]', '[data-create]', '[data-config]']) { const section = view.querySelector(selector)?.closest('section'); if (section) section.hidden = true; }
  window.MediaHubNativeUrl = safeNative(profile.JellyfinUrl).href.replace(/\/$/, '') + '/web/index.html';
  window.MediaHubOpenNative = url => sdk.commands.openExternalLink({ url: safeNative(url).href });
  window.ApiClient = {
    getCurrentUserId: () => profile.UserId, serverId: () => profile.ServerId || '', getUrl: route => path('Api/' + route.replace(/^MediaHub\//, '')),
    subscribe: () => () => {}, // Activity is not a native player; party creation/join routes are rejected server-side.
    ajax: options => call(options.url, options.data ? JSON.parse(options.data) : undefined),
    mediaHubTransfer: (route, options = {}) => call(path('Api/' + route), options.body, options.method !== 'POST', options.method)
  };
  if (run !== lifecycle) return;
  activeView = view; root.replaceChildren(view);
  for (const resource of ['mediahub.messenger.js', 'mediahub.js']) {
    await new Promise((resolve, reject) => { const script = document.createElement('script'); script.src = path('Assets/' + resource); script.onload = resolve; script.onerror = reject; document.head.append(script); });
  }
  if (run !== lifecycle) return;
  window.JellyfinMediaHub(view, {}); view.dispatchEvent(new Event('viewshow'));
}
async function boot() {
  const config = await call(path('Config')); sdk = new DiscordSDK(config.ApplicationId, { disableConsoleLogOverride: true }); await sdk.ready();
  const challenge = await call(path('Challenge'), {});
  const authorization = await sdk.commands.authorize({ client_id: config.ApplicationId, response_type: 'code', state: challenge.Challenge, prompt: 'none', scope: ['identify', 'guilds'] });
  const authenticated = await call(path('Authenticate'), { Code: authorization.code, Guild: sdk.guildId, Challenge: challenge.Challenge });
  session = authenticated.Session;
  const result = await sdk.commands.authenticate({ access_token: authenticated.AccessToken });
  if (result.user.id !== authenticated.Discord) throw new Error('Discord identity changed. Reopen Activity.');
  await sdk.subscribe('CURRENT_USER_UPDATE', user => { if (user.id !== authenticated.Discord) end('Discord account changed. Reopen Activity.'); });
  await startView();
}
window.addEventListener('pagehide', () => end('Activity closed.'), { once: true });
boot().catch(e => end(e.message));
