import { DiscordSDK } from '@discord/embedded-app-sdk';
import { node, formatBytes, reference } from './dom.js';
import './style.css';

const root = document.querySelector('#app');
const icons = { movie: '🎬', tv: '📺', music: '🎵', book: '📚' };
let sdk, token, config, profile, content, health, page = 'home', category = 'all', allRequests = false;
let searchQuery = '', searchKind = 'movie', searchPage = 1, linkGeneration, refreshTimer, linkTimer, loading = false;
let inboxKind = 'all', inboxGuild = null, inboxUser = null, inboxChannel = null, inboxQuery = '', inboxPage = 1, inboxOrder = 'newest';
let privacyGeneration = 0;
let queuedDestination = null;
const galleryState = new Map();
const posterObservers = new Map();
const posterUrls = new Map();
let dialogSequence = 0;

function setAppearance(value) {
  const appearance = value === 'light' ? 'light' : 'dark';
  document.documentElement.dataset.appearance = appearance;
  try { localStorage.setItem('media-appearance', appearance); } catch {}
}
try { document.documentElement.dataset.appearance = localStorage.getItem('media-appearance') === 'light' ? 'light' : 'dark'; } catch {}

function emptyState(title, description, action) {
  const block = node('div', 'empty');
  const symbol = node('span', 'empty-symbol', '◈'); symbol.setAttribute('aria-hidden', 'true');
  block.append(symbol, node('h2', '', title), node('p', '', description));
  if (action) block.append(action);
  return block;
}

function loadingState() {
  const block = node('div', 'loading-state'); block.setAttribute('role', 'status');
  block.append(node('p', 'loading', 'Refreshing live state…'));
  if (profile.account) {
    const grid = node('div', 'skeleton-grid'); grid.setAttribute('aria-hidden', 'true');
    for (let i = 0; i < 6; i++) grid.append(node('div', 'skeleton-card'));
    block.append(grid);
  }
  return block;
}

function releasePoster(url) { URL.revokeObjectURL(url); posterUrls.delete(url); }

function cleanupPosters() {
  for (const [observer, image] of posterObservers) {
    if (!image.isConnected) { observer.disconnect(); posterObservers.delete(observer); }
  }
  for (const [url, image] of posterUrls) if (!image.isConnected) releasePoster(url);
}
const compose = { initialized: false, message: '', guilds: new Set(), user: '', allUsers: false, channel: null };

async function request(path, body) {
  const response = await fetch(path, {
    method: body ? 'POST' : 'GET',
    headers: { 'Content-Type': 'application/json', ...(token ? { Authorization: `Bearer ${token}` } : {}) },
    body: body ? JSON.stringify(body) : undefined,
  });
  const data = await response.json();
  if (!response.ok) {
    const error = new Error(data.error || 'Unable to complete the action. Reopen the Activity if your session expired.');
    error.privateDataBlocked = data.private_data_blocked || response.status === 401;
    if (error.privateDataBlocked) hidePrivateData(data.verification_required);
    throw error;
  }
  return data;
}

async function api(operation, args = {}) {
  const data = await request('/api/activity/action', { operation, ...args });
  if (operation === 'browse') return data;
  return Object.hasOwn(data, 'result') ? data.result : data;
}

function button(text, handler, className = 'button') {
  const element = node('button', className, text);
  element.type = 'button';
  element.addEventListener('click', async () => {
    element.disabled = true;
    element.setAttribute('aria-busy', 'true');
    try { await handler(); } catch (error) { showError(error); }
    finally { element.disabled = element.dataset.blocked === 'true'; element.removeAttribute('aria-busy'); }
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

function hidePrivateData(relink = false) {
  privacyGeneration += 1;
  clearInterval(refreshTimer);
  profile = { account: null, devices: [], is_admin: false };
  compose.initialized = false; compose.guilds.clear(); compose.message = ''; compose.user = '';
  page = 'home';
  galleryState.clear(); queuedDestination = null;
  buildShell();
  cleanupPosters();
  if (relink) renderLink();
  else content.append(button('Retry verification', loadPage));
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
  const brand = node('div', 'brand');
  brand.append(node('span', 'brand-symbol', '◈'), node('span', '', 'Media'));
  sidebar.append(brand, node('p', 'nav-eyebrow', profile.account ? 'YOUR PERSONAL HUB' : 'PRIVATE BY DESIGN'));
  const navigation = node('nav');
  navigation.setAttribute('aria-label', 'Main navigation');
  const tabs = profile.account ? [['home', '◈ Home'], ['search', '⌕ Discover'], ['library', '▦ Library'], ['requests', '☷ Requests'], ['watches', '☆ Watchlist'], ['storage', '◉ Storage'], ['deletions', '◷ Deletions'], ['settings', '⚙ Preferences']] : [];
  if (profile.is_admin) tabs.push(['admin', '↗ Messages']);
  for (const [key, label] of tabs) {
    const tab = button(label, () => navigate(key), 'nav-button');
    tab.dataset.page = key;
    navigation.append(tab);
  }
  sidebar.append(navigation);
  const footer = node('div', 'sidebar-footer');
  if (profile.account) {
    const account = node('div', 'account-chip');
    const avatar = node('span', 'account-avatar', profile.account.name.slice(0, 1).toUpperCase()); avatar.setAttribute('aria-hidden', 'true');
    const identity = node('div'); identity.append(node('strong', '', profile.account.name), node('small', '', 'Your private media space'));
    account.append(avatar, identity); footer.append(account);
  }
  const appearance = button(document.documentElement.dataset.appearance === 'light' ? '☾ Dark appearance' : '☀ Light appearance', () => {
    setAppearance(document.documentElement.dataset.appearance === 'light' ? 'dark' : 'light');
    appearance.textContent = document.documentElement.dataset.appearance === 'light' ? '☾ Dark appearance' : '☀ Light appearance';
  }, 'appearance-button');
  appearance.setAttribute('aria-label', 'Switch color appearance'); footer.append(appearance); sidebar.append(footer);
  const main = node('main', 'main');
  const header = node('header', 'topbar');
  health = node('span', 'health', '● Verifying live data');
  health.setAttribute('aria-live', 'polite');
  header.append(health, button('↻ Refresh', loadPage, 'button subtle'));
  const notice = node('div', 'notice'); notice.id = 'notice'; notice.hidden = true; notice.setAttribute('role', 'status'); notice.setAttribute('aria-live', 'polite');
  content = node('section', 'content');
  content.id = 'main-content'; content.setAttribute('aria-label', 'Media content');
  main.append(header, notice, content);
  shell.append(sidebar, main);
  root.append(shell);
}

async function navigate(destination) {
  if (loading) { queuedDestination = destination; return; }
  page = destination;
  await loadPage();
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
  const generation = privacyGeneration;
  try {
    for (const tab of document.querySelectorAll('.nav-button')) {
      tab.classList.toggle('selected', tab.dataset.page === page);
      if (tab.dataset.page === page) tab.setAttribute('aria-current', 'page'); else tab.removeAttribute('aria-current');
    }
    content.setAttribute('aria-busy', 'true');
    content.replaceChildren(loadingState());
    cleanupPosters();
    const notice = document.querySelector('#notice'); notice.hidden = true;
    const wasLinked = Boolean(profile.account);
    if (!wasLinked || page === 'admin') {
      const freshProfile = await api('profile');
      if (generation !== privacyGeneration) return;
      profile = freshProfile;
    }
    if (!profile.account) { hidePrivateData(true); return; }
    if (page === 'admin' && !profile.is_admin) { page = 'home'; buildShell(); cleanupPosters(); }
    if (!wasLinked) buildShell();
    Object.assign(config, { refresh_interval: profile.refresh_interval, sync_interval: profile.sync_interval });
    if (page === 'admin' && profile.is_admin) {
      const [state, inbox] = await Promise.all([api('admin_state'), api('admin_inbox', {
        kind: inboxKind, guild_id: inboxGuild, user_id: inboxUser, channel_id: inboxChannel, query: inboxQuery, page: inboxPage, order: inboxOrder,
      })]);
      if (generation !== privacyGeneration) return;
      content.replaceChildren(); renderAdmin(state, inbox); health.textContent = '● Operator'; health.className = 'health'; return;
    }
    if (!profile.account) { content.replaceChildren(); renderLink(); return; }
    const bundle = await api('browse', { page, kind: page === 'search' ? searchKind : 'all',
      query: searchQuery, result_page: searchPage, all_requests: allRequests });
    if (generation !== privacyGeneration) return;
    const wasAdmin = profile.is_admin;
    profile = bundle.viewer;
    if (wasAdmin !== profile.is_admin) { buildShell(); cleanupPosters(); }
    Object.assign(config, { refresh_interval: profile.refresh_interval, sync_interval: profile.sync_interval });
    const result = bundle.result;
    content.replaceChildren();
    if (page === 'home') renderHome(result);
    else if (page === 'settings') renderSettings(profile);
    else if (page === 'storage') renderStorage(result);
    else if (page === 'search') renderSearch(result || []);
    else renderItems(result);
    if (['home', 'library', 'requests', 'storage'].includes(page)) updateHealth(bundle.meta);
    else { health.textContent = '● Account verified · Live access'; health.className = 'health'; }
  } catch (error) {
    if (!error.privateDataBlocked) content.replaceChildren(emptyState(page === 'admin' ? 'Messages unavailable' : 'Live data unavailable', 'Nothing private is shown until access can be checked. Try again when the connection is available.', button('Try again', loadPage, 'button primary')));
    showError(error);
  } finally {
    loading = false;
    content?.removeAttribute('aria-busy');
    if (queuedDestination !== null) {
      const destination = queuedDestination; queuedDestination = null;
      if (destination !== page) void navigate(destination);
    }
  }
}

function renderHome(data) {
  profile.account = data.account;
  heading(`Hi, ${data.account.name}`, 'A little less searching. A lot more enjoying.');
  const welcome = node('div', 'discovery-banner');
  welcome.append(node('p', 'eyebrow', 'YOUR NEXT GREAT FIND'), node('h2', '', 'Make time for\nsomething good.'), node('p', '', 'Discover a new favorite, revisit a classic, or pick up right where your curiosity left off.'),
    button('✨ Find something new', () => navigate('search'), 'button primary'), button('▶ Browse library', () => navigate('library'), 'button subtle'));
  content.append(welcome);
  const stats = node('div', 'stats');
  for (const [label, value, destination] of [['Your requests', data.requests, 'requests'], ['Available items', data.library, 'library'], ['Personal updates', data.account.opted_in ? 'On' : 'Off', 'settings']]) {
    const card = button('', () => navigate(destination), 'stat'); card.append(node('p', '', label), node('strong', '', value), node('span', 'stat-arrow', '↗')); stats.append(card);
  }
  content.append(stats);
  const shortcuts = node('div', 'shortcuts');
  for (const [kind, label] of [['movie', 'Movies'], ['tv', 'Series'], ['music', 'Music'], ['book', 'Books']]) {
    shortcuts.append(button(`${icons[kind]} ${label}`, async () => { category = kind; await navigate('library'); }, 'shortcut'));
  }
  content.append(shortcuts);
}

function select(options, current, onChange, label = 'Choose an option') {
  const element = node('select');
  element.setAttribute('aria-label', label);
  for (const [value, label] of options) { const option = node('option', '', label); option.value = value; element.append(option); }
  element.value = current;
  element.addEventListener('change', async () => {
    element.disabled = true;
    try { await onChange(element.value); } catch (error) { showError(error); }
    finally { element.disabled = false; }
  });
  return element;
}

function renderItems(items) {
  const titles = { library: ['Your library', 'Good things, all in one place. Browse your available movies, series, music and books.'], requests: ['Your requests', 'Keep up with the things you’re looking forward to.'], watches: ['Your watchlist', 'A place for your next favorites. Movies and series stay in sync with Seerr.'], deletions: ['Scheduled deletions', '24-hour delay · You can undo until execution begins.'] };
  heading(...titles[page]);
  const tools = node('div', 'toolbar');
  let redraw = () => {};
  if (page === 'library') tools.append(select([['all', 'Everything'], ['movie', 'Movies'], ['tv', 'Series'], ['music', 'Music'], ['book', 'Books']], category, async (value) => { category = value; redraw(); }, 'Library category'));
  if (page === 'requests') tools.append(select([['mine', 'My requests'], ['all', 'All requests']], allRequests ? 'all' : 'mine', async (value) => { allRequests = value === 'all'; await loadPage(); }, 'Request scope'));
  const stateKey = `${page}:${allRequests}`;
  if (!galleryState.has(stateKey)) galleryState.set(stateKey, { query: '', sort: 'default', page: 0 });
  const state = galleryState.get(stateKey);
  const filter = node('input'); filter.type = 'search'; filter.placeholder = 'Filter titles…'; filter.value = state.query; filter.setAttribute('aria-label', 'Filter collection'); tools.append(filter);
  tools.append(select([['default', 'Default order'], ['title', 'Title A–Z'], ['available', 'Available first']], state.sort, async value => { state.sort = value; state.page = 0; redraw(); }, 'Sort titles'));
  const count = node('span', 'count', `${items.length} items`); tools.append(count); content.append(tools);
  const grid = node('div', 'grid'); content.append(grid);
  const paging = node('div', 'toolbar gallery-paging'); content.append(paging);
  const draw = () => {
    const shown = items.filter(item => item.title.toLowerCase().includes(state.query.toLowerCase()) && (page !== 'library' || category === 'all' || item.kind === category));
    if (state.sort === 'title') shown.sort((a, b) => a.title.localeCompare(b.title));
    if (state.sort === 'available') shown.sort((a, b) => Number(Boolean(b.available)) - Number(Boolean(a.available)));
    const pages = Math.max(1, Math.ceil(shown.length / 12)); state.page = Math.min(state.page, pages - 1);
    count.textContent = `${shown.length} titles · Page ${state.page + 1}/${pages}`;
    grid.replaceChildren();
    cleanupPosters();
    for (const item of shown.slice(state.page * 12, state.page * 12 + 12)) grid.append(itemCard(item));
    if (!shown.length) {
      const empty = page === 'deletions' ? ['Nothing scheduled', 'Files stay right where they are. Any deletions you schedule will appear here with a 24-hour window to undo.'] :
        page === 'requests' ? ['Your next request starts here', 'Find something you love in Discover. You can follow its progress here.'] :
        page === 'watches' ? ['Save something for later', 'Follow titles in Discover or your library to build a watchlist that feels like you.'] :
        ['A little room for something new', 'Find something you love in Discover and make it your next request.'];
      grid.append(emptyState(state.query ? 'No matches just yet' : empty[0], state.query ? 'Try a shorter title or a different category.' : empty[1], page === 'deletions' ? undefined : button('Explore Discover', () => navigate('search'), 'button primary')));
    }
    const previous = button('← Previous', () => { state.page -= 1; draw(); }); previous.disabled = state.page === 0;
    const next = button('Next →', () => { state.page += 1; draw(); }); next.disabled = state.page + 1 >= pages;
    paging.replaceChildren(previous, node('span', 'fine', `${state.page + 1} / ${pages} · Open gallery snapshot`), next);
  };
  redraw = () => { state.page = 0; draw(); };
  filter.addEventListener('input', () => { state.query = filter.value; state.page = 0; draw(); }); draw();
}

function attachPoster(cover, item) {
  if (item.poster_path && /^\/[A-Za-z0-9_-]+\.(jpg|png|webp)$/.test(item.poster_path)) {
    const image = node('img'); image.alt = ''; image.width = 342; image.height = 513; image.decoding = 'async'; cover.append(image);
    // Image fetches carry the bearer session in headers, never in URLs or cookies.
    const load = () => fetch('/api/activity/poster' + item.poster_path, { headers: { Authorization: `Bearer ${token}` } }).then(async response => {
      if (!response.ok) {
        const data = await response.json();
        if (data.private_data_blocked || response.status === 401) hidePrivateData(data.verification_required);
        throw new Error('Image unavailable');
      }
      const blob = await response.blob();
      if (!image.isConnected) return;
      const url = URL.createObjectURL(blob);
      posterUrls.set(url, image);
      image.addEventListener('load', () => releasePoster(url), { once: true });
      image.addEventListener('error', () => { releasePoster(url); image.remove(); }, { once: true });
      image.src = url;
    }).catch(() => image.remove());
    if (typeof IntersectionObserver === 'function') {
      const observer = new IntersectionObserver(entries => {
        if (entries.some(entry => entry.isIntersecting)) { observer.disconnect(); posterObservers.delete(observer); void load(); }
        else if (!image.isConnected) { observer.disconnect(); posterObservers.delete(observer); }
      }, { rootMargin: '240px' }); posterObservers.set(observer, image); observer.observe(image);
    } else void load();
  }
}

function itemCard(item) {
  const card = node('article', 'media-card');
  const cover = button(icons[item.kind] || '◈', () => showDetails(item), `cover ${item.kind}`);
  cover.setAttribute('aria-label', `Details for ${item.title}`);
  attachPoster(cover, item);
  cover.append(node('span', 'cover-category', item.kind));
  if (item.available) cover.append(node('span', 'cover-availability', 'Available'));
  const body = node('div', 'card-body');
  const title = button(item.title, () => showDetails(item), 'card-title'); title.title = item.title;
  body.append(title, node('p', 'item-status', item.subtitle || (item.available ? 'Available' : 'Not yet available')));
  if (page === 'requests') body.append(node('p', 'fine', `#${item.id}${item.is4k ? ' · 4K' : ''}`));
  if (item.size) body.append(node('p', 'fine', formatBytes(item.size)));
  if (page === 'deletions') {
    if (item.execute_after) body.append(node('p', 'fine', `Scheduled ${new Date(item.execute_after * 1000).toLocaleString()}`));
    if (item.error) body.append(node('p', 'fine', item.error));
    body.append(itemActions(item));
  } else {
    body.append(itemActions(item));
  }
  card.append(cover, body); return card;
}

function itemActions(item, destination = page, close = () => {}) {
  const actions = node('div', 'card-actions');
  if (destination === 'deletions') {
    if (item.status === 'pending') actions.append(button('Undo', async () => { const result = await api('undo', { id: item.id }); close(); await loadPage(); toast(result); }, 'button primary'));
    return actions;
  }
  if (destination === 'search' && item.requestable !== false) actions.append(button('＋ Request', () => {
    close(); confirm('Request this item?', `${item.title}. TV: all seasons. Music/books: configured profiles.`, async () => { const result = await api('request', { item: reference(item), confirmed: true }); await loadPage(); toast(result); });
  }, 'button primary'));
  const follow = button(destination === 'watches' ? '☆ Unfollow' : '☆ Follow', async () => {
    const result = await api('watch', { item: reference(item), remove: destination === 'watches' });
    if (destination === 'watches') { close(); await loadPage(); }
    else { follow.textContent = '✓ Following'; follow.setAttribute('aria-pressed', 'true'); follow.dataset.blocked = 'true'; }
    toast(result);
  }, 'button subtle'); actions.append(follow);
  if (['movie', 'tv'].includes(item.kind) && item.available) actions.append(button('▶ Jellyfin', async () => { const url = await api('open', { item: reference(item) }); await sdk.commands.openExternalLink({ url }); }, 'button subtle'));
  if (destination === 'requests' && item.available && item.deletable) actions.append(button('Delete files…', () => {
    close(); confirm('Delete files in 24 hours?', `${item.title}${item.is4k ? ' (4K)' : ''}. Removes the entire movie/series, including shared files. Request history stays. Undo in Deletions until execution begins.`, async () => { const result = await api('delete', { id: item.id, confirmed: true }); await loadPage(); toast(result); });
  }, 'button danger'));
  return actions;
}

async function showDetails(item) {
  clearInterval(refreshTimer);
  const generation = privacyGeneration;
  const destination = page;
  const trigger = document.activeElement;
  const dialog = node('dialog', 'media-detail');
  dialog.setAttribute('aria-label', 'Title details');
  const close = () => { dialog.close(); dialog.remove(); cleanupPosters(); startRefresh(); if (trigger?.isConnected) trigger.focus(); };
  dialog.append(button('← Back to browsing', close, 'button subtle'), node('div', 'loading', 'Loading title details…'));
  dialog.addEventListener('cancel', event => { event.preventDefault(); close(); });
  root.append(dialog); dialog.showModal();
  try {
    const fresh = destination === 'deletions' ? (await api('deletions')).find(row => row.id === item.id) : await api('details', { item: reference(item) });
    if (generation !== privacyGeneration || !dialog.isConnected) return;
    if (!fresh) throw new Error('This item is no longer available. Refresh the gallery.');
    const detail = { ...item, ...fresh };
    const layout = node('div', 'detail-layout');
    const cover = node('div', `cover detail-cover ${detail.kind}`, icons[detail.kind]); attachPoster(cover, detail);
    const information = node('div', 'detail-information');
    information.append(node('p', 'fine', detail.kind.toUpperCase()), node('h2', '', detail.title), node('p', 'detail-overview', detail.overview || 'No synopsis available.'),
      node('p', 'pill', detail.available ? 'Available to watch' : 'Not yet available'));
    information.append(itemActions(detail, destination, close));
    layout.append(cover, information);
    const back = button('← Back to browsing', close, 'button subtle');
    dialog.replaceChildren(back, layout); back.focus();
  } catch (error) {
    if (dialog.isConnected) { close(); showError(error); }
    else showError(error);
  }
}

function renderSearch(items) {
  heading('Discover', searchQuery ? `Results for “${searchQuery}”` : 'Popular now · Powered by your Seerr account');
  const form = node('form', 'search-form');
  const kind = select([['movie', '🎬 Movies'], ['tv', '📺 Series'], ['music', '🎵 Albums'], ['book', '📚 Books']], searchKind, async (value) => { searchKind = value; searchPage = 1; await loadPage(); }, 'Discovery category');
  const query = node('input'); query.type = 'search'; query.maxLength = 100; query.required = true; query.placeholder = 'Title, artist, or author…'; query.value = searchQuery; query.setAttribute('aria-label', 'Search media');
  const submit = node('button', 'button primary', 'Search'); submit.type = 'submit';
  form.append(kind, query, submit);
  form.addEventListener('submit', async (event) => { event.preventDefault(); searchQuery = query.value.trim(); searchKind = kind.value; searchPage = 1; await loadPage(); }); content.append(form);
  const shortcuts = node('div', 'toolbar');
  if (searchQuery) shortcuts.append(button('← Popular titles', async () => { searchQuery = ''; searchPage = 1; await loadPage(); }, 'button subtle'));
  shortcuts.append(button('Open Seerr ↗', async () => { const url = await api('seerr_link'); await sdk.commands.openExternalLink({ url }); }, 'button subtle'));
  content.append(shortcuts);
  const grid = node('div', 'grid'); for (const item of items) grid.append(itemCard(item)); content.append(grid);
  if (!items.length) content.append(emptyState(searchQuery ? 'No results this time' : 'Find your next favorite', ['music', 'book'].includes(searchKind) && !searchQuery ? 'Search by title, artist or author to get started.' : 'Try another title or category. Your next favorite could be one search away.'));
  if (['movie', 'tv'].includes(searchKind)) {
    const controls = node('div', 'toolbar');
    const prev = button('← Previous results', async () => { searchPage = Math.max(1, searchPage - 1); await loadPage(); }); prev.disabled = searchPage === 1;
    const next = button('Next results →', async () => { searchPage = Math.min(500, searchPage + 1); await loadPage(); }); next.disabled = !items.length || searchPage === 500;
    controls.append(prev, node('span', 'fine', `Result page ${searchPage}`), next); content.append(controls);
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
  if (data.devices.length) devices.append(select(data.devices.map(name => [name, name]), profile.account.device || data.devices[0], async (device) => { const result = await api('preferences', { device }); await loadPage(); toast(result); }, 'Jellyfin destination'));
  else devices.append(node('p', 'fine', 'Configure JELLYFIN_DEVICE_URLS in the bot environment.'));
  const watchlist = node('article', 'setting');
  watchlist.append(node('h2', '', 'Two-way watchlist sync'),
    node('p', '', 'Movies and series sync with Seerr automatically, including removals. Music/books stay here. Following does not request media.'),
    button('Sync now', async () => { const result = await api('sync_watchlist'); await loadPage(); toast(result); }, 'button subtle'));
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
        if (status.state === 'linked') { clearInterval(linkTimer); profile = await api('profile'); buildShell(); await loadPage(); startRefresh(); toast('Connected. Your verified Discord ID is saved in Seerr.'); }
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
  const trigger = document.activeElement;
  const heading = node('h2', '', title); heading.id = `confirmation-${++dialogSequence}`; dialog.setAttribute('aria-labelledby', heading.id);
  dialog.append(node('p', 'eyebrow', 'ONE LAST CHECK'), heading, node('p', '', description));
  const actions = node('div', 'card-actions');
  let pending = false;
  const close = () => { if (pending) return; dialog.close(); dialog.remove(); startRefresh(); if (trigger?.isConnected) trigger.focus(); };
  const cancel = button('Cancel', close, 'button subtle');
  const submit = button('Confirm', async () => {
    pending = true; cancel.disabled = true; dialog.setAttribute('aria-busy', 'true');
    try { await action(); pending = false; close(); }
    catch (error) {
      pending = false;
      if (dialog.isConnected) {
        const notice = node('p', 'notice error', `${error.message} Check the current state before trying again.`); notice.setAttribute('role', 'alert');
        dialog.append(notice); submit.hidden = true; submit.dataset.blocked = 'true';
      }
    } finally { pending = false; cancel.disabled = false; dialog.removeAttribute('aria-busy'); }
  }, 'button primary');
  actions.append(cancel, submit);
  dialog.append(actions); dialog.addEventListener('cancel', (event) => { event.preventDefault(); close(); }); root.append(dialog); dialog.showModal(); cancel.focus();
}

function startRefresh() {
  clearInterval(refreshTimer);
  refreshTimer = setInterval(() => {
    const editing = content?.contains(document.activeElement) && document.activeElement.matches('input,textarea,select');
    if (!document.hidden && !editing && !document.querySelector('dialog[open]') && (profile?.account || profile?.is_admin && page === 'admin') && !loading && page !== 'search') void loadPage();
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
  Object.assign(config, { refresh_interval: profile.refresh_interval || 60, sync_interval: profile.sync_interval || 60 });
  buildShell(); await loadPage(); startRefresh();
}

boot().catch(error => showError(error));
