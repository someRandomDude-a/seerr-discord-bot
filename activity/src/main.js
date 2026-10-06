import { DiscordSDK } from '@discord/embedded-app-sdk';
import { node, formatBytes, reference } from './dom.js';
import './style.css';

const root = document.querySelector('#app');
const icons = { movie: '🎬', tv: '📺', music: '🎵', book: '📚' };
let sdk, token, config, profile, content, health, page = 'home', category = 'all', allRequests = false;
let searchQuery = '', searchKind = 'movie', searchPage = 1, linkGeneration, refreshTimer, linkTimer, loading = false;
let inboxKind = 'all', inboxGuild = null, inboxUser = null, inboxChannel = null, inboxQuery = '', inboxPage = 1, inboxOrder = 'newest';
const compose = { initialized: false, message: '', guilds: new Set(), user: '', allUsers: false, channel: null };

async function request(path, body) {
  const response = await fetch(path, {
    method: body ? 'POST' : 'GET',
    headers: { 'Content-Type': 'application/json', ...(token ? { Authorization: `Bearer ${token}` } : {}) },
    body: body ? JSON.stringify(body) : undefined,
  });
  const data = await response.json();
  if (!response.ok) throw new Error(data.error || 'Unable to complete the action. Reopen the Activity if your session expired.');
  return data;
}

async function api(operation, args = {}) {
  const data = await request('/api/activity/action', { operation, ...args });
  return Object.hasOwn(data, 'result') ? data.result : data;
}

function button(text, handler, className = 'button') {
  const element = node('button', className, text);
  element.type = 'button';
  element.addEventListener('click', async () => {
    element.disabled = true;
    try { await handler(); } catch (error) { showError(error); }
    finally { element.disabled = false; }
  });
  return element;
}

function showError(error) {
  const message = error instanceof Error ? error.message : 'Action could not be confirmed.';
  const target = document.querySelector('#notice');
  if (target) {
    target.replaceChildren(node('span', '', message));
    target.className = 'notice error';
    target.hidden = false;
  } else root.replaceChildren(node('div', 'standalone error', message));
  if (health) { health.textContent = '● Live verification needed'; health.className = 'health error'; }
}

function toast(message) {
  const target = document.querySelector('#notice');
  target.textContent = message.replace(/<t:(\d+):[FR]>/g, (_, seconds) => new Date(Number(seconds) * 1000).toLocaleString());
  target.className = 'notice success';
  target.hidden = false;
}

function buildShell() {
  root.replaceChildren();
  const shell = node('div', 'shell');
  const sidebar = node('aside', 'sidebar');
  sidebar.append(node('div', 'brand', '◈ Media'));
  const navigation = node('nav');
  const tabs = [['home', '◈ Overview'], ['library', '▦ Library'], ['requests', '☷ Requests'], ['search', '⌕ Discover'], ['watches', '☆ Watchlist'], ['storage', '◉ Storage'], ['deletions', '◷ Deletions'], ['settings', '⚙ Preferences']];
  if (profile.is_admin) tabs.push(['admin', '↗ Messages']);
  for (const [key, label] of tabs) {
    const tab = button(label, async () => { if (loading) return; page = key; await loadPage(); }, 'nav-button');
    tab.dataset.page = key;
    navigation.append(tab);
  }
  sidebar.append(navigation);
  const main = node('main', 'main');
  const header = node('header', 'topbar');
  health = node('span', 'health', '● Verifying live data');
  header.append(health, button('↻ Refresh', loadPage, 'button subtle'));
  const notice = node('div', 'notice'); notice.id = 'notice'; notice.hidden = true;
  content = node('section', 'content');
  main.append(header, notice, content);
  shell.append(sidebar, main);
  root.append(shell);
}

function heading(title, subtitle) {
  const block = node('div', 'page-heading');
  block.append(node('h1', '', title));
  if (subtitle) block.append(node('p', 'lead', subtitle));
  content.append(block);
}

function updateHealth(meta) {
  const timestamp = Number(meta.last_success || 0);
  const healthy = timestamp && !meta.error && Date.now() / 1000 - timestamp <= config.sync_interval * 2;
  health.textContent = healthy ? `● Verified ${new Date(timestamp * 1000).toLocaleTimeString()}` : '● Verification unavailable';
  health.className = healthy ? 'health' : 'health error';
}

async function loadPage() {
  if (loading) return;
  loading = true;
  try {
    for (const tab of document.querySelectorAll('.nav-button')) tab.classList.toggle('selected', tab.dataset.page === page);
    content.replaceChildren(node('div', 'loading', 'Refreshing live state…'));
    const notice = document.querySelector('#notice'); notice.hidden = true;
    if (page === 'admin' && profile.is_admin) {
      const [state, inbox] = await Promise.all([api('admin_state'), api('admin_inbox', {
        kind: inboxKind, guild_id: inboxGuild, user_id: inboxUser, channel_id: inboxChannel, query: inboxQuery, page: inboxPage, order: inboxOrder,
      })]);
      content.replaceChildren(); renderAdmin(state, inbox); health.textContent = '● Operator'; health.className = 'health'; return;
    }
    if (!profile.account) { content.replaceChildren(); renderLink(); return; }
    let result;
    if (page === 'home') result = await api('dashboard');
    else if (page === 'settings') result = await api('profile');
    else if (page === 'search') result = searchQuery ? await api('search', { kind: searchKind, query: searchQuery, page: searchPage }) : [];
    else if (page === 'library') result = await api('library', { kind: category });
    else if (page === 'requests') result = await api('requests', { all_requests: allRequests });
    else result = await api(page);
    content.replaceChildren();
    if (page === 'home') renderHome(result);
    else if (page === 'settings') renderSettings(result);
    else if (page === 'storage') renderStorage(result);
    else if (page === 'search') renderSearch(result);
    else renderItems(result);
    updateHealth(await api('status'));
  } catch (error) {
    content.replaceChildren(node('div', 'empty', page === 'admin' ? 'Messages unavailable' : 'Live data unavailable · Try refreshing'));
    showError(error);
  } finally { loading = false; }
}

function renderHome(data) {
  profile.account = data.account;
  heading(`Hi, ${data.account.name}`);
  const stats = node('div', 'stats');
  for (const [label, value] of [['Your requests', data.requests], ['Available items', data.library], ['Personal updates', data.account.opted_in ? 'On' : 'Off']]) {
    const card = node('div', 'stat'); card.append(node('p', '', label), node('strong', '', value)); stats.append(card);
  }
  content.append(stats);
  const shortcuts = node('div', 'shortcuts');
  for (const [kind, label] of [['movie', 'Movies'], ['tv', 'Series'], ['music', 'Music'], ['book', 'Books']]) {
    shortcuts.append(button(`${icons[kind]} ${label}`, async () => { category = kind; page = 'library'; await loadPage(); }, 'shortcut'));
  }
  content.append(shortcuts);
}

function select(options, current, onChange) {
  const element = node('select');
  for (const [value, label] of options) { const option = node('option', '', label); option.value = value; element.append(option); }
  element.value = current;
  element.addEventListener('change', async () => {
    try { await onChange(element.value); } catch (error) { showError(error); }
  });
  return element;
}

function renderItems(items) {
  const titles = { library: ['Library'], requests: ['Requests'], watches: ['Watchlist'], deletions: ['Deletions', '24-hour delay · Undo until execution'] };
  heading(...titles[page]);
  const tools = node('div', 'toolbar');
  if (page === 'library') tools.append(select([['all', 'Everything'], ['movie', 'Movies'], ['tv', 'Series'], ['music', 'Music'], ['book', 'Books']], category, async (value) => { category = value; await loadPage(); }));
  if (page === 'requests') tools.append(select([['mine', 'Mine'], ['all', 'All']], allRequests ? 'all' : 'mine', async (value) => { allRequests = value === 'all'; await loadPage(); }));
  const filter = node('input'); filter.type = 'search'; filter.placeholder = 'Filter…'; filter.setAttribute('aria-label', 'Filter collection'); tools.append(filter);
  const count = node('span', 'count', `${items.length} items`); tools.append(count); content.append(tools);
  const grid = node('div', 'grid'); content.append(grid);
  const draw = () => {
    const shown = items.filter(item => item.title.toLowerCase().includes(filter.value.toLowerCase()));
    grid.replaceChildren();
    for (const item of shown) grid.append(itemCard(item));
    if (!shown.length) grid.append(node('div', 'empty', 'No items'));
  };
  filter.addEventListener('input', draw); draw();
}

function itemCard(item) {
  const card = node('article', 'media-card');
  const cover = node('div', `cover ${item.kind}`, icons[item.kind] || '◈');
  if (item.poster_path && /^\/[A-Za-z0-9_-]+\.(jpg|png|webp)$/.test(item.poster_path)) {
    const image = node('img'); image.loading = 'lazy'; image.alt = ''; image.src = '/api/activity/poster' + item.poster_path;
    image.addEventListener('error', () => image.remove()); cover.append(image);
  }
  cover.append(node('span', 'cover-category', item.kind));
  const body = node('div', 'card-body');
  body.append(node('h3', '', item.title), node('p', 'item-status', item.subtitle || (item.available ? 'Available' : 'Not yet available')));
  if (page === 'requests') body.append(node('p', 'fine', `#${item.id}${item.is4k ? ' · 4K' : ''}`));
  if (item.size) body.append(node('p', 'fine', formatBytes(item.size)));
  if (page === 'deletions') {
    if (item.execute_after) body.append(node('p', 'fine', `Scheduled ${new Date(item.execute_after * 1000).toLocaleString()}`));
    if (item.error) body.append(node('p', 'fine', item.error));
    if (item.status === 'pending') body.append(button('Undo', async () => { const result = await api('undo', { id: item.id }); await loadPage(); toast(result); }, 'button primary full'));
  } else {
    const actions = node('div', 'card-actions');
    if (page === 'search' && item.requestable !== false) actions.append(button('＋ Request', () => confirm('Request this item?', `${item.title}. TV: all seasons. Music/books: configured profiles.`, async () => { const result = await api('request', { item: reference(item), confirmed: true }); await loadPage(); toast(result); }), 'button primary'));
    actions.append(button(page === 'watches' ? '☆ Unfollow' : '☆ Follow', async () => { const result = await api('watch', { item: reference(item), remove: page === 'watches' }); toast(result); if (page === 'watches') await loadPage(); }, 'button subtle'));
    if (item.jellyfin_id) actions.append(button('▶ Jellyfin', async () => { const url = await api('open', { item: reference(item) }); await sdk.commands.openExternalLink({ url }); }, 'button subtle'));
    body.append(actions);
    if (page === 'requests' && item.available && item.deletable) body.append(button('Delete files…', () => confirm('Delete files in 24 hours?', `${item.title}${item.is4k ? ' (4K)' : ''}. Removes the entire movie/series, including shared files. Request history stays. Undo in Deletions until execution begins.`, async () => { const result = await api('delete', { id: item.id, confirmed: true }); await loadPage(); toast(result); }), 'button danger full'));
  }
  card.append(cover, body); return card;
}

function renderSearch(items) {
  heading('Discover');
  const form = node('form', 'search-form');
  const kind = select([['movie', '🎬 Movies'], ['tv', '📺 Series'], ['music', '🎵 Albums'], ['book', '📚 Books']], searchKind, async (value) => { searchKind = value; searchPage = 1; });
  const query = node('input'); query.type = 'search'; query.maxLength = 100; query.required = true; query.placeholder = 'Title, artist, or author…'; query.value = searchQuery; query.setAttribute('aria-label', 'Search media');
  const submit = node('button', 'button primary', 'Search'); submit.type = 'submit';
  form.append(kind, query, submit);
  form.addEventListener('submit', async (event) => { event.preventDefault(); searchQuery = query.value.trim(); searchKind = kind.value; searchPage = 1; await loadPage(); }); content.append(form);
  const grid = node('div', 'grid'); for (const item of items) grid.append(itemCard(item)); content.append(grid);
  if (searchQuery && !items.length) content.append(node('div', 'empty', 'No results'));
  if (searchQuery && ['movie', 'tv'].includes(searchKind)) {
    const controls = node('div', 'toolbar');
    const prev = button('← Previous results', async () => { searchPage = Math.max(1, searchPage - 1); await loadPage(); }); prev.disabled = searchPage === 1;
    controls.append(prev, node('span', 'fine', `Search page ${searchPage}`), button('Next results →', async () => { searchPage = Math.min(500, searchPage + 1); await loadPage(); })); content.append(controls);
  }
}

function renderStorage(disks) {
  heading('Storage', 'Shared volumes may appear more than once');
  const list = node('div', 'disk-list');
  for (const [source, volumes] of Object.entries(disks)) {
    for (const disk of volumes) {
      const row = node('article', 'disk');
      const total = Number(disk.totalSpace || 0), free = Number(disk.freeSpace || 0);
      const used = total ? Math.min(100, Math.max(0, (1 - free / total) * 100)) : 0;
      row.append(node('h3', '', `${source.toUpperCase()} · ${disk.path || 'Volume'}`));
      const progress = node('progress'); progress.max = 100; progress.value = used; progress.setAttribute('aria-label', 'Used storage');
      row.append(progress, node('p', '', `${formatBytes(free)} free of ${formatBytes(total)} · ${used.toFixed(0)}% used`)); list.append(row);
    }
  }
  if (!list.children.length) list.append(node('div', 'empty', 'Configure media-service URLs and API keys to report live storage.'));
  content.append(list);
}

function renderSettings(data) {
  profile = data;
  heading('Preferences');
  const settings = node('div', 'settings-list');
  const notifications = node('article', 'setting');
  notifications.append(node('h2', '', 'Notifications'), node('p', '', 'DMs for requests and watched items'), node('span', 'pill', profile.account.opted_in ? 'On' : 'Off'));
  notifications.append(button('Enable', async () => { await api('preferences', { enabled: true }); await loadPage(); }, 'button primary'), button('Mute', async () => { await api('mute'); await loadPage(); }, 'button subtle'));
  const devices = node('article', 'setting');
  devices.append(node('h2', '', 'Jellyfin device'));
  if (data.devices.length) devices.append(select(data.devices.map(name => [name, name]), profile.account.device || data.devices[0], async (device) => { toast(await api('preferences', { device })); }));
  else devices.append(node('p', 'fine', 'Configure JELLYFIN_DEVICE_URLS in the bot environment.'));
  const watchlist = node('article', 'setting');
  watchlist.append(node('h2', '', 'Seerr watchlist'), button('Import', async () => toast(await api('import_watchlist')), 'button subtle'));
  settings.append(notifications, devices, watchlist); content.append(settings);
}

function renderLink() {
  heading('Connect Jellyfin');
  const card = node('div', 'link-card');
  card.append(node('div', 'link-symbol', '🔐'), node('p', '', 'Approve a code in Jellyfin → Settings → Quick Connect.'), button('Get code', async () => {
    const result = await api('link_start'); linkGeneration = result.generation;
    card.replaceChildren(node('div', 'connection-code', result.code), node('p', '', `Approve in Jellyfin · ${result.expires_in}s · Keep private`), button('Cancel', async () => { await api('link_cancel', { generation: linkGeneration }); clearInterval(linkTimer); await loadPage(); }, 'button subtle'));
    clearInterval(linkTimer);
    linkTimer = setInterval(async () => {
      try {
        const status = await api('link_status', { generation: linkGeneration });
        if (status.state === 'linked') { clearInterval(linkTimer); profile = await api('profile'); await loadPage(); toast('Connected. Your verified Discord ID is saved in Seerr.'); }
        else if (status.state === 'expired') { clearInterval(linkTimer); await loadPage(); showError(new Error('Code expired. Generate a new one.')); }
      } catch (error) { clearInterval(linkTimer); showError(error); }
    }, 5000);
  }, 'button primary'));
  content.append(card);
}

function renderAdmin(state, inbox) {
  heading('Messages');
  if (!compose.initialized) {
    if (sdk.guildId && state.guilds.some(guild => guild.id === sdk.guildId)) compose.guilds.add(sdk.guildId);
    compose.initialized = true;
  }
  const layout = node('div', 'admin-layout');
  const composer = node('section', 'setting compose');
  composer.append(node('h2', '', 'Send'));
  const message = node('textarea'); message.maxLength = 2000; message.rows = 5; message.placeholder = 'Message…'; message.value = compose.message; message.setAttribute('aria-label', 'Announcement message');
  message.addEventListener('input', () => { compose.message = message.value; }); composer.append(message);
  const user = node('input'); user.placeholder = 'User ID (optional DM)'; user.inputMode = 'numeric'; user.value = compose.user; user.setAttribute('aria-label', 'Direct message user ID');
  user.addEventListener('input', () => { compose.user = user.value.trim(); compose.channel = null; }); composer.append(user);
  const guildList = node('div', 'guild-checklist');
  for (const guild of state.guilds) {
    const label = node('label', 'check-row');
    const check = node('input'); check.type = 'checkbox'; check.checked = compose.guilds.has(guild.id);
    check.addEventListener('change', () => { if (check.checked) compose.guilds.add(guild.id); else compose.guilds.delete(guild.id); compose.channel = null; });
    label.append(check, node('span', '', guild.name));
    if (!guild.channel) label.append(node('span', 'fine', 'No channel'));
    guildList.append(label);
  }
  composer.append(guildList);
  if (compose.channel) composer.append(node('p', 'fine', `Reply to #${inbox.channels.find(channel => channel.channel_id === compose.channel)?.channel_name || compose.channel}`));
  composer.append(button('Select all servers', () => { for (const guild of state.guilds) compose.guilds.add(guild.id); renderAdminAgain(state, inbox); }, 'button subtle'));
  const broadcast = node('label', 'check-row');
  const everyone = node('input'); everyone.type = 'checkbox'; everyone.checked = compose.allUsers; everyone.disabled = !state.all_users_enabled;
  everyone.addEventListener('change', () => { compose.allUsers = everyone.checked; });
  broadcast.append(everyone, node('span', '', 'Also DM all members')); composer.append(broadcast);
  composer.append(button('Preview send', async () => {
    const messagePreview = compose.message;
    const plan = await api('admin_prepare', { message: messagePreview, guild_ids: [...compose.guilds],
      user_id: compose.user || null, all_users: compose.allUsers, channel_id: compose.channel });
    const skipped = plan.skipped.length ? ` · ${plan.skipped.length} unavailable` : '';
    confirm('Send message?', `${plan.channels} channels · ${plan.users} user DMs${skipped}\n${plan.destinations.join('\n')}\n\n${messagePreview}\n\nMentions disabled.`, async () => {
      const sent = await api('admin_send', { plan: plan.plan, confirmed: true }); compose.message = ''; await loadPage(); toast(`Send #${sent.job} started.`);
    });
  }, 'button primary full'));
  if (state.jobs.length) {
    const jobs = node('div', 'send-jobs');
    for (const job of state.jobs.slice(0, 5)) jobs.append(node('p', 'fine', `#${job.id} · ${job.sent} sent · ${job.pending} pending${job.failed ? ` · ${job.failed} blocked` : ''}${job.uncertain ? ` · ${job.uncertain} unconfirmed` : ''}`));
    composer.append(jobs);
  }
  const received = node('section', 'setting inbox-panel'); received.append(node('h2', '', 'Inbox'), node('span', 'fine', `${state.retention_days} days · Up to ${state.max_messages.toLocaleString()} messages`));
  const filters = node('div', 'inbox-filters');
  const resetPage = async () => { inboxPage = 1; await loadPage(); };
  filters.append(select([['all', 'All'], ['dm', 'DMs'], ['guild', 'Servers']], inboxKind, async value => { inboxKind = value; inboxGuild = null; inboxUser = null; inboxChannel = null; await resetPage(); }));
  const serverFilter = select([['', 'Server'], ...state.guilds.map(guild => [guild.id, guild.name])], inboxGuild || '', async value => { inboxGuild = value || null; inboxKind = value ? 'guild' : inboxKind; inboxChannel = null; await resetPage(); });
  serverFilter.disabled = inboxKind === 'dm'; serverFilter.setAttribute('aria-label', 'Filter by server'); filters.append(serverFilter);
  const channels = inbox.channels.filter(channel => !inboxGuild || channel.guild_id === inboxGuild);
  const channelFilter = select([['', 'Channel'], ...channels.map(channel => [channel.channel_id, `#${channel.channel_name}`])], inboxChannel || '', async value => { inboxChannel = value || null; if (value) inboxKind = 'guild'; await resetPage(); });
  channelFilter.disabled = inboxKind === 'dm'; channelFilter.setAttribute('aria-label', 'Filter by channel'); filters.append(channelFilter);
  filters.append(select([['', 'Sender'], ...inbox.users.map(sender => [sender.author_id, sender.author_name])], inboxUser || '', async value => { inboxUser = value || null; await resetPage(); }));
  filters.append(select([['newest', 'Newest first'], ['oldest', 'Oldest first']], inboxOrder, async value => { inboxOrder = value; await resetPage(); }));
  filters.append(button('Reset', async () => { inboxKind = 'all'; inboxGuild = inboxUser = inboxChannel = null; inboxQuery = ''; inboxOrder = 'newest'; await resetPage(); }, 'button subtle'));
  const search = node('form', 'inbox-search');
  const query = node('input'); query.type = 'search'; query.maxLength = 100; query.placeholder = 'Search messages…'; query.value = inboxQuery; query.setAttribute('aria-label', 'Search received messages');
  const searchButton = node('button', 'button subtle', 'Search'); searchButton.type = 'submit'; search.append(query, searchButton);
  search.addEventListener('submit', async event => { event.preventDefault(); inboxQuery = query.value.trim(); await resetPage(); });
  received.append(filters, search);
  const inboxLayout = node('div', 'inbox-layout');
  const threads = node('nav', 'thread-list');
  for (const [kind, label] of [['dm', 'DMs'], ['guild', 'Servers']]) {
    if (inboxKind !== 'all' && inboxKind !== kind) continue;
    const group = state.threads.filter(thread => thread.kind === kind);
    if (!group.length) continue;
    threads.append(node('h3', 'thread-heading', label));
    for (const thread of group) {
      const threadButton = button(`${kind === 'dm' ? '◉' : '#'} ${thread.name} · ${thread.count}`, async () => {
        inboxKind = thread.kind; inboxGuild = kind === 'guild' ? thread.id : null; inboxUser = kind === 'dm' ? thread.id : null; inboxChannel = null; await resetPage();
      }, 'thread-button');
      threadButton.title = thread.id;
      if (kind === 'dm' && inboxUser === thread.id || kind === 'guild' && inboxGuild === thread.id) threadButton.classList.add('selected');
      threads.append(threadButton);
    }
  }
  const messages = node('div', 'received-list');
  for (const item of inbox.messages) {
    const card = node('article', 'received-message');
    const meta = node('div', 'message-meta');
    const sender = node('strong', '', item.author_name); sender.title = item.author_id;
    meta.append(sender, node('span', 'fine', item.guild_name ? `${item.guild_name} / #${item.channel_name}` : 'DM'), node('time', 'fine', new Date(item.created_at * 1000).toLocaleString()));
    card.append(meta, node('p', 'received-content', item.content || (item.attachment_count ? 'Attachment' : 'No text')));
    if (item.attachment_count) card.append(node('span', 'fine', `${item.attachment_count} attachment${item.attachment_count > 1 ? 's' : ''}`));
    const actions = node('div', 'card-actions');
    actions.append(button('Reply', () => {
      compose.user = item.guild_id ? '' : item.author_id;
      compose.guilds = new Set(item.guild_id ? [item.guild_id] : []);
      compose.channel = item.guild_id ? item.channel_id : null;
      compose.allUsers = false;
      renderAdminAgain(state, inbox);
      content.querySelector('textarea').focus();
    }, 'button subtle'));
    if (item.guild_id) actions.append(button('Open', async () => {
      const url = `https://discord.com/channels/${item.guild_id}/${item.channel_id}/${item.message_id}`;
      await sdk.commands.openExternalLink({ url });
    }, 'button subtle'));
    card.append(actions);
    messages.append(card);
  }
  if (!inbox.messages.length) messages.append(node('div', 'empty', 'No messages'));
  inboxLayout.append(threads, messages); received.append(inboxLayout);
  const pagination = node('div', 'toolbar');
  const previous = button('←', async () => { inboxPage = Math.max(1, inboxPage - 1); await loadPage(); }); previous.disabled = inboxPage === 1;
  const next = button('→', async () => { inboxPage += 1; await loadPage(); }); next.disabled = inboxPage * 50 >= inbox.total;
  pagination.append(previous, node('span', 'fine', `${inboxPage}/${Math.max(1, Math.ceil(inbox.total / 50))} · ${inbox.total}`), next); received.append(pagination);
  layout.append(composer, received); content.append(layout);
}

function renderAdminAgain(state, inbox) {
  content.replaceChildren(); renderAdmin(state, inbox);
}

function confirm(title, description, action) {
  clearInterval(refreshTimer);  // Do not replace confirmation while it is being reviewed.
  const dialog = node('dialog', 'confirm-dialog');
  dialog.append(node('h2', '', title), node('p', '', description));
  const actions = node('div', 'card-actions');
  const close = () => { dialog.close(); dialog.remove(); startRefresh(); };
  actions.append(button('Cancel', close, 'button subtle'), button('Confirm', async () => { close(); await action(); }, 'button primary'));
  dialog.append(actions); dialog.addEventListener('cancel', (event) => { event.preventDefault(); close(); }); root.append(dialog); dialog.showModal();
}

function startRefresh() {
  clearInterval(refreshTimer);
  refreshTimer = setInterval(() => {
    const editing = content?.contains(document.activeElement) && document.activeElement.matches('input,textarea,select');
    if (!document.hidden && !editing && (profile?.account || profile?.is_admin && page === 'admin') && !loading && page !== 'search') void loadPage();
  }, config.refresh_interval * 1000);
}

async function boot() {
  root.append(node('div', 'standalone', '◈ Connecting to Discord…'));
  config = await request('/api/activity/config');
  sdk = new DiscordSDK(config.application_id);
  await sdk.ready();
  const { code } = await sdk.commands.authorize({ client_id: config.application_id, response_type: 'code', state: crypto.randomUUID(), prompt: 'none', scope: config.scopes });
  const authenticated = await request('/api/activity/auth', { code, guild_id: sdk.guildId });
  await sdk.commands.authenticate({ access_token: authenticated.access_token });
  token = authenticated.session_token; // Memory only. No localStorage, cookies, URLs, or logs.
  profile = await api('profile');
  buildShell(); await loadPage(); startRefresh();
}

boot().catch(error => showError(error));
