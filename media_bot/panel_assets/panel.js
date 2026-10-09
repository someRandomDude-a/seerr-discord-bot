import { mountMessenger } from './messenger.js';

const root = document.querySelector('#app');
let state, tab = 'setup', messenger, messengerGeneration = 0, authGeneration = 0;
const draft = new Map(), clear = new Set();
const reports = new Map(), profileChoices = new Map();
document.body.dataset.ui = 'panel';
try { document.documentElement.dataset.appearance = localStorage.getItem('media-appearance') === 'light' ? 'light' : 'dark'; } catch {}

function appearanceControl() {
  const control = button(document.documentElement.dataset.appearance === 'light' ? 'Dark appearance' : 'Light appearance', () => {
    const appearance = document.documentElement.dataset.appearance === 'light' ? 'dark' : 'light';
    document.documentElement.dataset.appearance = appearance;
    try { localStorage.setItem('media-appearance', appearance); } catch {}
    control.textContent = appearance === 'light' ? 'Dark appearance' : 'Light appearance';
  }, 'button subtle appearance-button');
  control.setAttribute('aria-label', 'Switch color appearance'); return control;
}
function updateDraftIndicator() {
  const indicator = document.querySelector('#draft-status');
  if (indicator) {
    const count = new Set([...draft.keys(), ...clear]).size;
    indicator.textContent = count ? `${count} unsaved ${count === 1 ? 'change' : 'changes'}` : 'All changes saved';
    indicator.dataset.dirty = String(Boolean(count));
  }
}
const labels = { DISCORD_TOKEN: 'Bot token', ALLOWED_GUILD_IDS: 'Allowed servers', ADMIN_DISCORD_IDS: 'Bot-only admin IDs',
  ENABLE_MEMBERS_INTENT: 'All-member DMs', INBOX_MESSAGE_CONTENT: 'Full inbox content', SEERR_URL: 'Seerr URL',
  SEERR_ADMIN_KEY: 'Seerr API key', JELLYFIN_DEVICE_URLS: 'Jellyfin links', ACTIVITY_ENABLED: 'Activity',
  DISCORD_APPLICATION_ID: 'Application ID', DISCORD_CLIENT_SECRET: 'OAuth client secret', WEBHOOK_PUBLIC_URL: 'Public backend URL' };
const steps = [
  ['1 · Discord', 'discord', ['DISCORD_TOKEN', 'ALLOWED_GUILD_IDS', 'ADMIN_DISCORD_IDS', 'ENABLE_MEMBERS_INTENT', 'INBOX_MESSAGE_CONTENT']],
  ['2 · Seerr', 'seerr', ['SEERR_URL', 'SEERR_ADMIN_KEY', 'JELLYFIN_DEVICE_URLS']],
  ['3 · Music', 'lidarr', ['LIDARR_URL', 'LIDARR_API_KEY', 'LIDARR_ROOT_FOLDER', 'LIDARR_QUALITY_PROFILE_ID', 'LIDARR_METADATA_PROFILE_ID']],
  ['4 · Books', 'readarr', ['READARR_URL', 'READARR_API_KEY', 'READARR_ROOT_FOLDER', 'READARR_QUALITY_PROFILE_ID', 'READARR_METADATA_PROFILE_ID']],
  ['5 · Hosting', null, ['ACTIVITY_ENABLED', 'DISCORD_APPLICATION_ID', 'DISCORD_CLIENT_SECRET', 'WEBHOOK_PUBLIC_URL']],
];

function node(tag, text, cls) {
  const element = document.createElement(tag);
  if (text !== undefined) element.textContent = text;
  if (cls) element.className = cls;
  return element;
}
async function api(path, data) {
  const generation = authGeneration;
  const multipart = data instanceof FormData;
  const response = await fetch(path, { method: data ? 'POST' : 'GET', credentials: 'same-origin',
    headers: data && !multipart ? { 'Content-Type': 'application/json' } : {}, body: multipart ? data : data ? JSON.stringify(data) : undefined });
  const body = await response.json();
  if (generation !== authGeneration) throw new Error('Panel session changed. Retry after signing in.');
  if (!response.ok) {
    let message = body.error || 'Not confirmed';
    if (response.status === 401 && path !== '/api/login') {
      const wasConnected = Boolean(state);
      if (wasConnected) message = 'Panel session expired or the service restarted. Sign in with the current console access code.';
      renderLogin(wasConnected ? message : '');
    }
    const failure = new Error(message); failure.status = response.status;
    throw failure;
  }
  return body;
}
function renderLogin(message = '') {
  authGeneration += 1;
  messenger?.dispose(); messenger = null; messengerGeneration += 1;
  state = undefined; tab = 'setup'; draft.clear(); clear.clear(); reports.clear(); profileChoices.clear();
  const section = node('section', undefined, 'setting login');
  const mark = node('div', '◈', 'login-mark'); mark.setAttribute('aria-hidden', 'true');
  section.append(mark, node('p', 'MEDIA · PRIVATE ADMIN', 'eyebrow'), node('h1', 'Welcome home.'),
    node('p', 'Your media. Your conversations. One calm place to manage it all.', 'login-intro'));
  const form = node('form'); form.id = 'login';
  const label = node('label', 'Console access code', 'panel-label'); label.htmlFor = 'access-code';
  const input = node('input'); input.name = 'code'; input.type = 'password'; input.autocomplete = 'off'; input.required = true;
  input.id = 'access-code'; input.placeholder = 'Paste your one-time code'; input.spellcheck = false;
  input.setAttribute('aria-label', 'Console access code');
  const connect = node('button', 'Connect', 'button primary'); connect.type = 'submit';
  form.append(label, input, node('small', 'Enter the current one-time access code printed by the service.', 'fine'), connect);
  const notice = node('p', message, 'notice error'); notice.id = 'login-error'; notice.hidden = !message;
  notice.setAttribute('role', 'alert');
  section.append(form, notice, node('small', 'Private by design. Keep this code to yourself. Use the same panel URL each time; expired sessions print a replacement code in the console. If a still-valid cookie was lost, restart the service for a new code.', 'login-security'), appearanceControl());
  root.replaceChildren(section);
  form.addEventListener('submit', async event => {
    event.preventDefault();
    if (connect.disabled) return;
    // An older bootstrap/live request must not invalidate a new sign-in.
    authGeneration += 1;
    connect.disabled = true; connect.textContent = 'Connecting…'; connect.setAttribute('aria-busy', 'true'); notice.hidden = true;
    try {
      await api('/api/login', { code: input.value.trim() });
      input.value = '';
      state = await api('/api/state'); render();
    } catch (exc) { error(exc.message); }
    finally { connect.disabled = false; connect.textContent = 'Connect'; connect.removeAttribute('aria-busy'); }
  });
}
function error(message) {
  let target = document.querySelector('#notice') || document.querySelector('#login-error');
  if (target) { target.hidden = false; target.textContent = message; target.className = 'notice error'; }
}
function button(label, callback, cls = 'button') {
  const element = node('button', label, cls); element.type = 'button';
  element.addEventListener('click', async () => {
    element.disabled = true;
    element.setAttribute('aria-busy', 'true');
    try { await callback(); } catch (exc) { error(exc.message); }
    finally { element.disabled = false; element.removeAttribute('aria-busy'); }
  });
  return element;
}
function field(key) {
  const spec = state.fields.find(f => f.key === key);
  const label = node('div', undefined, 'panel-field');
  const caption = node('label', labels[key] || key.replaceAll('_', ' ').toLowerCase(), 'panel-label'); caption.htmlFor = `field-${key}`;
  label.append(caption);
  const choices = profileChoices.get(key);
  const input = choices || spec.default === 'true' || spec.default === 'false' ? node('select') : node(spec.default.startsWith('{') ? 'textarea' : 'input');
  if (input.tagName === 'SELECT') {
    const options = choices ? [[key.endsWith('_ID') ? '0' : '', 'Not selected · browse only'], ...choices] : [['false', 'Off'], ['true', 'On']];
    for (const [value, text] of options) { const option = node('option', text); option.value = value; input.append(option); }
  } else if (spec.secret) { input.type = 'password'; input.autocomplete = 'off'; input.placeholder = state.values[key] ? 'Saved · leave blank to keep' : 'Not configured'; }
  input.dataset.key = key;
  input.id = caption.htmlFor;
  input.setAttribute('aria-label', labels[key] || key);
  input.value = draft.get(key) ?? (spec.secret ? '' : state.values[key]);
  if (!choices && input.tagName === 'SELECT') input.value = input.value.trim().toLowerCase();
  input.disabled = state.locked.includes(key);
  input.addEventListener('input', () => { draft.set(key, input.value); updateDraftIndicator(); });
  if (input.disabled) label.append(node('span', 'Environment managed', 'field-lock'));
  label.append(input, node('small', spec.help + (input.disabled ? ' Environment override: remove it to edit here.' : '')));
  if (spec.secret && state.values[key] && !input.disabled) {
    const clearing = node('label', undefined, 'fine'); const check = node('input', undefined, 'clear-secret'); check.type = 'checkbox'; check.checked = clear.has(key);
    check.addEventListener('change', () => { if (check.checked) clear.add(key); else clear.delete(key); updateDraftIndicator(); });
    clearing.append(check, node('span', 'Clear saved credential')); label.append(clearing);
  }
  return label;
}
function changes() { return { changes: Object.fromEntries(draft), clear: [...clear] }; }
async function action(operation, output) {
  const result = await api('/api/action', { operation, ...changes() });
  state = result; draft.clear(); clear.clear();
  updateDraftIndicator();
  reports.set(operation, report(result.result));
  if (result.result?.choices) {
    const prefix = operation.toUpperCase();
    for (const [suffix, collection, key] of [['_QUALITY_PROFILE_ID', 'qualityprofile', 'id'], ['_METADATA_PROFILE_ID', 'metadataprofile', 'id'], ['_ROOT_FOLDER', 'rootfolder', 'path']]) {
      profileChoices.set(prefix + suffix, result.result.choices[collection].map(row => [String(row[key]), row.name || row.path]));
    }
  }
  if (output) output.textContent = report(result.result);
  return result.result;
}
function report(result) {
  if (result?.guilds) return `${result.bot} · ${result.application_id}\n` + result.guilds.map(g => `${g.name} · ${g.id}`).join('\n') + '\nInstall: ' + result.install_url;
  if (result?.services) return result.message + '\n' + result.services.map(s => `${s.name} · ${s.url}`).join('\n') + '\nSeerr admins: ' + result.admins.map(u => `${u.name} (#${u.id})`).join(', ');
  if (result?.choices) return result.message + '\n' + Object.entries(result.choices).map(([type, rows]) => `${type}:\n${rows.map(r => `${r.id} · ${r.name || r.path}`).join('\n')}`).join('\n');
  if (result?.disabled) return 'Disabled · optional';
  if (result?.secret) return result.message + '\nBearer ' + result.secret;
  return result?.message || Object.values(result || {}).join('\n');
}
function shell() {
  root.replaceChildren();
  root.dataset.tab = tab;
  const top = node('header', undefined, 'panel-top');
  const brand = node('div', undefined, 'panel-brand');
  brand.append(node('span', '◈', 'brand-symbol'), node('h1', 'Media'), node('span', 'CONTROL ROOM', 'eyebrow')); top.append(brand);
  const navigation = node('nav', undefined, 'panel-tabs');
  navigation.setAttribute('aria-label', 'Admin navigation');
  for (const [id, label] of [['setup', 'Setup'], ['messages', 'Messages'], ['advanced', 'Advanced'], ['help', 'Help']]) {
    const control = button(label, async () => { tab = id; render(); if (tab === 'messages') await loadMessages(); }, 'button ' + (tab === id ? 'primary' : 'subtle'));
    if (tab === id) control.setAttribute('aria-current', 'page'); navigation.append(control);
  }
  const status = node('span', state.running ? '● Connected' : '● Setup pending', 'panel-status'); status.dataset.connected = String(Boolean(state.running));
  top.append(navigation, status, appearanceControl()); root.append(top);
  const notice = node('div', '', 'notice'); notice.id = 'notice'; notice.hidden = true; notice.setAttribute('role', 'status'); notice.setAttribute('aria-live', 'polite'); root.append(notice);
  if (state.runtime_error) { notice.textContent = state.runtime_error; notice.className = 'notice error'; notice.hidden = false; }
  if (state.activity_authenticated) root.append(node('p', '● Discord Activity authentication verified', 'fine'));
  if (Object.keys(state.webhook_tests || {}).length) root.append(node('p', 'Receiver tests: ' + Object.entries(state.webhook_tests).map(([source, time]) => `${source} ${new Date(time * 1000).toLocaleTimeString()}`).join(' · '), 'fine'));
}
function render() {
  if (!state) return;
  messenger?.dispose(); messenger = null; messengerGeneration += 1;
  shell();
  const headings = { setup: ['Make yourself at home.', 'Connect your services, choose your experience, and let Media take care of the rest.'],
    messages: ['Conversations, connected.', 'A private space for your bot’s DMs, server conversations and announcements.'],
    advanced: ['Fine-tune your space.', 'Find any setting without losing your changes. Environment-managed values stay read-only.'],
    help: ['A little guidance.', 'Everything you need to get connected, stay private and send with confidence.'] };
  const intro = node('section', undefined, 'panel-intro');
  intro.append(node('p', 'YOUR PRIVATE CONTROL ROOM', 'eyebrow'), node('h2', headings[tab][0]), node('p', headings[tab][1], 'lead')); root.append(intro);
  if (tab === 'messages') { root.append(node('div', 'Loading messages…', 'loading')); return; }
  if (tab === 'help') { renderHelp(); return; }
  if (tab === 'advanced') {
    const search = node('input'); search.type = 'search'; search.placeholder = 'Search settings by name or description…'; search.setAttribute('aria-label', 'Search settings'); search.className = 'settings-search'; root.append(search);
    const grid = node('div', undefined, 'panel-grid');
    for (const spec of state.fields) {
      const wrapper = node('section', undefined, 'setting panel-step'); wrapper.dataset.search = `${spec.key} ${labels[spec.key] || ''} ${spec.help}`.toLowerCase(); wrapper.append(field(spec.key)); grid.append(wrapper);
    }
    const empty = node('p', 'No matching settings. Try a shorter name.', 'empty'); empty.hidden = true;
    search.addEventListener('input', () => {
      let count = 0;
      for (const section of grid.children) { section.hidden = !section.dataset.search.includes(search.value.trim().toLowerCase()); if (!section.hidden) count += 1; }
      empty.hidden = count > 0;
    });
    root.append(empty);
    root.append(grid); saveControls(); return;
  }
  const presets = node('section', undefined, 'setting panel-step mode-section'); presets.append(node('p', 'START WITH YOUR STYLE', 'eyebrow'), node('h2', 'How would you like to use Media?'));
  const modes = node('div', undefined, 'mode-grid');
  for (const [preset, activity, webhook, description] of [['Slash only', 'false', false, 'Everything you need, right inside Discord. No public hosting.'], ['Activity', 'true', false, 'A beautiful embedded dashboard alongside your slash commands.'], ['Slash + webhooks', 'false', true, 'Native Discord with service events waking fresh updates.'], ['Activity + webhooks', 'true', true, 'The full experience: embedded browsing and event-driven refresh.']]) {
    const choice = node('article', undefined, 'mode-card');
    const selected = String(draft.get('ACTIVITY_ENABLED') ?? state.values.ACTIVITY_ENABLED).toLowerCase() === activity && Boolean(state.values.WEBHOOK_SECRET && !clear.has('WEBHOOK_SECRET')) === webhook;
    choice.classList.toggle('selected', selected);
    const control = button(preset, async () => {
      draft.set('ACTIVITY_ENABLED', activity);
      if (webhook && !state.values.WEBHOOK_SECRET) { await action('generate_webhook'); }
      else if (!webhook && state.values.WEBHOOK_SECRET) clear.add('WEBHOOK_SECRET');
      render();
    }, 'button subtle'); control.setAttribute('aria-pressed', String(selected));
    choice.append(control, node('p', description, 'fine')); modes.append(choice);
  }
  presets.append(modes);
  presets.append(node('p', 'Slash commands always work. Activity needs public HTTPS; this private panel never does.', 'fine')); root.append(presets);
  const grid = node('div', undefined, 'panel-grid');
  for (const [title, operation, keys] of steps) {
    const optional = operation === 'lidarr' || operation === 'readarr';
    const section = node(optional ? 'details' : 'section', undefined, 'setting panel-step' + (optional ? ' optional-step' : !operation ? ' hosting-step' : ''));
    if (optional) section.open = Boolean((draft.get(operation.toUpperCase() + '_URL') ?? state.values[operation.toUpperCase() + '_URL']) || reports.has(operation));
    const header = node(optional ? 'summary' : 'div', undefined, 'step-heading');
    header.append(node('span', title.split(' · ')[0], 'step-number'), node('h2', title.split(' · ')[1]));
    const verified = operation && state.verified.includes(operation);
    header.append(node('span', verified ? '✓ Tested' : operation === 'lidarr' || operation === 'readarr' ? 'Optional' : 'Setup', 'step-state' + (verified ? ' verified' : ''))); section.append(header);
    if (verified) section.append(node('span', 'Save rechecks that tested settings have not changed.', 'fine'));
    const fields = node('div', undefined, 'step-fields');
    for (const key of keys) fields.append(field(key)); section.append(fields);
    const output = node('div', reports.get(operation) || '', 'panel-report');
    if (operation) section.append(button('Verify', async () => { await action(operation, output); render(); }));
    else {
      section.append(node('p', 'Activity: enable Activities in Discord, add OAuth redirect https://127.0.0.1, and map / to your HTTPS hostname. Enter the shared backend hostname above for the external test. Never proxy this panel.', 'fine'));
      section.append(button('Generate webhook credential', async () => { await action('generate_webhook', output); }));
      section.append(button('Show webhook credential', async () => { await action('webhook_info', output); }));
      section.append(button('Test public backend', async () => { await action('public_test', output); }));
    }
    section.append(output); grid.append(section);
  }
  root.append(grid); saveControls();
}
function saveControls() {
  const controls = node('div', undefined, 'panel-actions');
  const indicator = node('span', '', 'draft-status'); indicator.id = 'draft-status'; indicator.setAttribute('role', 'status'); controls.append(indicator);
  controls.append(button('Save & apply', async () => {
    await action('save'); render();
    const notice = document.querySelector('#notice'); notice.hidden = false; notice.className = 'notice success'; notice.textContent = 'Saved. Bot is starting/restarting. Refresh status shortly.';
  }, 'button primary'), button('Refresh status', async () => { state = await api('/api/state'); render(); }));
  controls.append(node('span', 'Saved settings survive restarts. Secrets are never read back into forms.', 'fine')); root.append(controls);
  updateDraftIndicator();
}
function renderHelp() {
  const section = node('section', undefined, 'setting panel-help');
  section.append(node('h2', 'Checklist'));
  for (const text of [
    '1. Create a Discord application → Bot → copy bot token. Install scopes: bot + applications.commands.',
    '2. Invite to your server. Grant View Channels, Send Messages, Embed Links, Read Message History; Threads if needed. No Administrator/Manage Roles permission.',
    '3. Members Intent is only for all-member DMs. Message Content is only for full non-mention replies/configured inbox channels. Enable in Discord and here.',
    '4. Enter Seerr URL and administrator API key. Configure Jellyfin Quick Connect in Seerr/Jellyfin. Radarr/Sonarr are imported from Seerr, including 4K instances.',
    '5. Seerr owners/admins become bot admins after /link verifies their Discord identity. Bot-only exception IDs grant global bot inbox/send access without modifying Seerr.',
    '6. Lidarr/Readarr are optional direct connections. Verify, then choose root/quality/metadata IDs from the test results; verify again after changing them.',
    '7. Save & apply. In Discord test /link, /status, /dashboard, /inbox and a single-recipient /announce. Confirm only expected sends. Use disposable media for deletion tests.',
    '8. Activity additionally needs a built frontend, client secret, HTTPS tunnel/proxy to the bot backend, / URL Mapping, and placeholder OAuth redirect https://127.0.0.1. Use /activity to verify the actual SDK/OAuth flow.',
    '9. Webhooks additionally need a reachable receiver and generated credential. Configure service URLs /webhooks/seerr or /webhooks/radarr|sonarr|lidarr|readarr. Use Bearer auth or Basic bot/password credential. Test in every service.',
    '10. The local panel is a separate HTTP port. Native port 0 is random; Docker publishes a fixed container port to a random host-loopback port. Use SSH forwarding from remote hosts. Never publish it in the Activity URL Mapping.',
    'Settings reference: .env.advanced.example. Full instructions: docs/onboarding.md and docs/setup.md. Treat access codes, settings.json, database and backups as sensitive.',
  ]) section.append(node('p', text));
  root.append(section);
}
async function loadMessages() {
  const version = ++messengerGeneration;
  root.querySelector('.loading')?.remove();
  const host = node('div'); root.append(host);
  const mounted = await mountMessenger(host, api, error);
  if (version !== messengerGeneration || tab !== 'messages') mounted.dispose();
  else messenger = mounted;
}
renderLogin();
api('/api/state').then(value => { state = value; render(); }).catch(exc => {
  if (exc.status !== 401 && exc.message !== 'Panel session changed. Retry after signing in.') error('Unable to reach the panel. Check the SSH tunnel and try connecting again.');
});
