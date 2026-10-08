const root = document.querySelector('#app');
let state, tab = 'setup', inboxPage = 1, selectedChannel = null;
const draft = new Map(), clear = new Set();
const reports = new Map(), profileChoices = new Map();
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
  const response = await fetch(path, { method: data ? 'POST' : 'GET', credentials: 'same-origin',
    headers: data ? { 'Content-Type': 'application/json' } : {}, body: data ? JSON.stringify(data) : undefined });
  const body = await response.json();
  if (!response.ok) throw new Error(body.error || 'Not confirmed');
  return body;
}
function error(message) {
  let target = document.querySelector('#notice') || document.querySelector('#login-error');
  if (target) { target.hidden = false; target.textContent = message; target.className = 'notice error'; }
}
function button(label, callback, cls = 'button') {
  const element = node('button', label, cls); element.type = 'button';
  element.addEventListener('click', async () => {
    element.disabled = true;
    try { await callback(); } catch (exc) { error(exc.message); }
    finally { element.disabled = false; }
  });
  return element;
}
function field(key) {
  const spec = state.fields.find(f => f.key === key);
  const label = node('div', undefined, 'panel-field');
  label.append(node('span', labels[key] || key.replaceAll('_', ' ').toLowerCase(), 'panel-label'));
  const choices = profileChoices.get(key);
  const input = choices || spec.default === 'true' || spec.default === 'false' ? node('select') : node(spec.default.startsWith('{') ? 'textarea' : 'input');
  if (input.tagName === 'SELECT') {
    const options = choices ? [[key.endsWith('_ID') ? '0' : '', 'Not selected · browse only'], ...choices] : [['false', 'Off'], ['true', 'On']];
    for (const [value, text] of options) { const option = node('option', text); option.value = value; input.append(option); }
  } else if (spec.secret) { input.type = 'password'; input.autocomplete = 'off'; input.placeholder = state.values[key] ? 'Saved · leave blank to keep' : 'Not configured'; }
  input.dataset.key = key;
  input.setAttribute('aria-label', labels[key] || key);
  input.value = draft.get(key) ?? (spec.secret ? '' : state.values[key]);
  if (!choices && input.tagName === 'SELECT') input.value = input.value.trim().toLowerCase();
  input.disabled = state.locked.includes(key);
  input.addEventListener('input', () => { draft.set(key, input.value); });
  label.append(input, node('small', spec.help + (input.disabled ? ' Environment override: remove it to edit here.' : '')));
  if (spec.secret && state.values[key] && !input.disabled) {
    const clearing = node('label', undefined, 'fine'); const check = node('input', undefined, 'clear-secret'); check.type = 'checkbox'; check.checked = clear.has(key);
    check.addEventListener('change', () => { if (check.checked) clear.add(key); else clear.delete(key); });
    clearing.append(check, node('span', 'Clear saved credential')); label.append(clearing);
  }
  return label;
}
function changes() { return { changes: Object.fromEntries(draft), clear: [...clear] }; }
async function action(operation, output) {
  const result = await api('/api/action', { operation, ...changes() });
  state = result; draft.clear(); clear.clear();
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
  const top = node('header', undefined, 'panel-top'); top.append(node('h1', 'Media · Admin'));
  const navigation = node('nav', undefined, 'panel-tabs');
  for (const [id, label] of [['setup', 'Setup'], ['messages', 'Messages'], ['advanced', 'Advanced'], ['help', 'Help']]) {
    navigation.append(button(label, async () => { tab = id; render(); if (tab === 'messages') await loadMessages(); }, 'button ' + (tab === id ? 'primary' : 'subtle')));
  }
  top.append(navigation); root.append(top);
  const notice = node('div', '', 'notice'); notice.id = 'notice'; notice.hidden = true; root.append(notice);
  root.append(node('p', state.running ? '● Connected' : state.runtime_error || '● Setup pending', 'panel-status'));
  if (state.activity_authenticated) root.append(node('p', '● Discord Activity authentication verified', 'fine'));
  if (Object.keys(state.webhook_tests || {}).length) root.append(node('p', 'Receiver tests: ' + Object.entries(state.webhook_tests).map(([source, time]) => `${source} ${new Date(time * 1000).toLocaleTimeString()}`).join(' · '), 'fine'));
}
function render() {
  shell();
  if (tab === 'messages') { root.append(node('div', 'Loading messages…', 'loading')); return; }
  if (tab === 'help') { renderHelp(); return; }
  if (tab === 'advanced') {
    const grid = node('div', undefined, 'panel-grid');
    for (const spec of state.fields) {
      const wrapper = node('section', undefined, 'setting panel-step'); wrapper.append(field(spec.key)); grid.append(wrapper);
    }
    root.append(grid); saveControls(); return;
  }
  const presets = node('section', undefined, 'setting panel-step'); presets.append(node('h2', 'Choose a mode'));
  for (const [preset, activity, webhook] of [['Slash only', 'false', false], ['Activity', 'true', false], ['Slash + webhooks', 'false', true], ['Activity + webhooks', 'true', true]]) {
    presets.append(button(preset, async () => {
      draft.set('ACTIVITY_ENABLED', activity);
      if (webhook && !state.values.WEBHOOK_SECRET) { await action('generate_webhook'); }
      else if (!webhook && state.values.WEBHOOK_SECRET) clear.add('WEBHOOK_SECRET');
      render();
    }, 'button subtle'));
  }
  presets.append(node('p', 'Slash commands always work. Activity needs public HTTPS; this private panel never does.', 'fine')); root.append(presets);
  const grid = node('div', undefined, 'panel-grid');
  for (const [title, operation, keys] of steps) {
    const section = node('section', undefined, 'setting panel-step'); section.append(node('h2', title));
    if (operation && state.verified.includes(operation)) section.append(node('span', 'Tested · save rechecks that settings have not changed', 'fine'));
    for (const key of keys) section.append(field(key));
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
  controls.append(button('Save & apply', async () => {
    await action('save'); render();
    const notice = document.querySelector('#notice'); notice.hidden = false; notice.className = 'notice success'; notice.textContent = 'Saved. Bot is starting/restarting. Refresh status shortly.';
  }, 'button primary'), button('Refresh status', async () => { state = await api('/api/state'); render(); }));
  controls.append(node('span', 'Saved settings survive restarts. Secrets are never read back into forms.', 'fine')); root.append(controls);
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
  selectedChannel = null;
  const [admin, inbox] = await Promise.all([api('/api/admin', { operation: 'state' }), api('/api/admin', { operation: 'inbox', ...inboxFilters() })]);
  shell();
  const grid = node('div', undefined, 'panel-grid');
  const composer = node('section', undefined, 'setting'); composer.append(node('h2', 'Send'));
  const message = node('textarea', undefined, 'panel-draft'); message.maxLength = 2000; message.placeholder = 'Message…'; composer.append(message);
  const user = node('input'); user.placeholder = 'User ID · optional DM'; user.id = 'recipient'; user.addEventListener('input', () => { selectedChannel = null; }); composer.append(user);
  const recipients = node('div', undefined, 'panel-recipients');
  for (const guild of admin.result.guilds) {
    const label = node('label'); const check = node('input'); check.type = 'checkbox'; check.value = guild.id;
    check.addEventListener('change', () => { selectedChannel = null; });
    label.append(check, node('span', guild.name + (guild.channel ? ' · #' + guild.channel : ' · no channel'))); recipients.append(label);
  }
  composer.append(recipients);
  composer.append(button('Select all servers', () => { selectedChannel = null; for (const check of recipients.querySelectorAll('input')) check.checked = true; }));
  const allLabel = node('label', undefined, 'check-row'); const all = node('input'); all.type = 'checkbox'; all.disabled = !admin.result.all_users_enabled;
  allLabel.append(all, node('span', 'Also DM all members')); composer.append(allLabel);
  composer.append(button('Preview', async () => {
    const plan = (await api('/api/admin', { operation: 'prepare', message: message.value,
      guild_ids: [...recipients.querySelectorAll('input:checked')].map(check => check.value), user_id: user.value.trim() || null,
      channel_id: selectedChannel, all_users: all.checked })).result;
    const dialog = node('dialog', undefined, 'confirm-dialog'); dialog.append(node('h2', 'Send?'), node('p', `${plan.channels} channels · ${plan.users} DMs\n${plan.destinations.join('\n')}\n${plan.skipped.length} unavailable\n\n${plan.message}\n\nMentions disabled.`));
    const close = () => { dialog.close(); dialog.remove(); };
    dialog.append(button('Cancel', close), button('Confirm', async () => { await api('/api/admin', { operation: 'send', plan: plan.plan, confirmed: true }); close(); await loadMessages(); }, 'button primary')); root.append(dialog); dialog.showModal();
  }, 'button primary full'));
  for (const job of admin.result.jobs) composer.append(button(`#${job.id} · ${job.sent} sent · ${job.pending} pending · ${job.failed} blocked · ${job.uncertain} unconfirmed`, () => showDeliveries(job.id), 'button subtle full'));
  const received = node('section', undefined, 'setting'); received.append(node('h2', 'Inbox'));
  const form = inboxForm(); received.append(form);
  const threads = node('div', undefined, 'panel-tabs');
  for (const kind of ['dm', 'guild']) {
    threads.append(node('span', kind === 'dm' ? 'DMs' : 'Servers', 'fine'));
    for (const thread of admin.result.threads.filter(t => t.kind === kind)) threads.append(button(`${thread.name} · ${thread.count}`, async () => {
      filters.kind = kind; filters.guild_id = kind === 'guild' ? thread.id : null; filters.user_id = kind === 'dm' ? thread.id : null; filters.channel_id = null; inboxPage = 1; await loadMessages();
    }, 'button subtle'));
  }
  received.append(threads);
  const list = node('div', undefined, 'panel-messages');
  for (const item of inbox.result.messages) {
    const card = node('article', undefined, 'received-message');
    card.append(node('strong', item.author_name), node('p', `${item.guild_name || 'DM'}${item.guild_id ? ' / #' + item.channel_name : ''} · ${new Date(item.created_at * 1000).toLocaleString()} · user ${item.author_id}`, 'fine'), node('p', item.content || 'No text', 'received-content'));
    if (item.attachment_count) card.append(node('span', `${item.attachment_count} attachments`, 'fine'));
    card.append(button('Reply', () => {
      for (const check of recipients.querySelectorAll('input')) check.checked = check.value === item.guild_id;
      user.value = item.guild_id ? '' : item.author_id; selectedChannel = item.guild_id ? item.channel_id : null; all.checked = false; message.focus();
    })); list.append(card);
  }
  if (!inbox.result.messages.length) list.append(node('p', 'No messages', 'empty'));
  received.append(list);
  const controls = node('div', undefined, 'panel-actions');
  const previous = button('←', async () => { inboxPage = Math.max(1, inboxPage - 1); await loadMessages(); }); previous.disabled = inboxPage === 1;
  const next = button('→', async () => { inboxPage += 1; await loadMessages(); }); next.disabled = inboxPage * 50 >= inbox.result.total;
  controls.append(previous, node('span', `${inboxPage} · ${inbox.result.total} messages`, 'fine'), next, button('Refresh', loadMessages)); received.append(controls);
  grid.append(composer, received); root.append(grid);
}
const filters = { kind: 'all', guild_id: null, user_id: null, channel_id: null, query: '', order: 'newest' };
async function showDeliveries(job) {
  let page = 1;
  const dialog = node('dialog', undefined, 'confirm-dialog'); root.append(dialog);
  async function load() {
    const { result } = await api('/api/admin', { operation: 'deliveries', job, page });
    dialog.replaceChildren(node('h2', `Delivery #${job}`), node('p', result.announcement.content));
    for (const row of result.rows) dialog.append(node('p', `${row.label} · ${row.status}\n${row.error || ''}`, 'panel-report'));
    const previous = button('←', async () => { page -= 1; await load(); }); previous.disabled = page === 1;
    const next = button('→', async () => { page += 1; await load(); }); next.disabled = page * 5 >= result.total;
    dialog.append(previous, node('span', ` ${page} · ${result.total} recipients `, 'fine'), next,
      button('Refresh', load), button('Close', () => { dialog.close(); dialog.remove(); }));
  }
  try { await load(); dialog.showModal(); } catch (exc) { dialog.remove(); throw exc; }
}
function inboxFilters() { return { ...filters, page: inboxPage }; }
function inboxForm() {
  const form = node('form', undefined, 'panel-inbox-tools');
  for (const [key, label] of [['kind', 'Kind'], ['guild_id', 'Server ID'], ['channel_id', 'Channel ID'], ['user_id', 'Sender ID'], ['query', 'Search text'], ['order', 'Order']]) {
    const input = key === 'kind' || key === 'order' ? node('select') : node('input'); input.name = key; input.placeholder = label; input.setAttribute('aria-label', label);
    if (input.tagName === 'SELECT') for (const value of key === 'kind' ? ['all', 'dm', 'guild'] : ['newest', 'oldest']) { const option = node('option', value); option.value = value; input.append(option); }
    input.value = filters[key] || ''; form.append(input);
  }
  const submit = node('button', 'Filter', 'button'); submit.type = 'submit'; form.append(submit);
  form.addEventListener('submit', async event => { event.preventDefault(); for (const [key, value] of new FormData(form)) filters[key] = ['guild_id', 'channel_id', 'user_id'].includes(key) ? value || null : value; inboxPage = 1; try { await loadMessages(); } catch (exc) { error(exc.message); } });
  return form;
}
document.querySelector('#login').addEventListener('submit', async event => {
  event.preventDefault();
  try { await api('/api/login', { code: new FormData(event.target).get('code') }); state = await api('/api/state'); render(); }
  catch (exc) { error(exc.message); }
});
api('/api/state').then(value => { state = value; render(); }).catch(() => {});
