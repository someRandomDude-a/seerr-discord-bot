import { chatMessage, chatAttachments, discordContent } from './chat.js';

export async function mountMessenger(host, api, reportError) {
  let disposed = false, source, admin, generation = 0, page = 1, query = '', active = { kind: 'all', name: 'Inbox' };
  let mode = 'normal', recipients = { guilds: [], user: null, channel: null, all: false }, refreshBusy = false, refreshAgain = false;
  const drafts = new Map();
  // getRandomValues also works on private HTTP origins where randomUUID may not.
  const nonce = () => typeof crypto.randomUUID === 'function' ? crypto.randomUUID() :
    Array.from(crypto.getRandomValues(new Uint8Array(24)), byte => byte.toString(16).padStart(2, '0')).join('');
  const el = (tag, text, cls) => { const node = document.createElement(tag); if (text !== undefined) node.textContent = text; if (cls) node.className = cls; return node; };
  const btn = (text, fn, cls = 'button subtle') => {
    const button = el('button', text, cls); button.type = 'button';
    button.addEventListener('click', async () => { button.disabled = true; button.setAttribute('aria-busy', 'true'); try { await fn(); } catch (error) { reportError(error.message); } finally { button.disabled = false; button.removeAttribute('aria-busy'); } });
    return button;
  };
  const shell = el('section', undefined, 'chat-shell');
  const sidebar = el('aside', undefined, 'chat-sidebar');
  sidebar.id = 'conversation-list'; sidebar.setAttribute('aria-label', 'Conversations');
  const main = el('section', undefined, 'chat-main');
  const header = el('header', undefined, 'chat-header');
  const timeline = el('div', undefined, 'chat-timeline'); timeline.setAttribute('role', 'log'); timeline.setAttribute('aria-live', 'polite');
  const composer = el('div', undefined, 'chat-composer');
  const paging = el('div', undefined, 'chat-paging');
  main.append(header, paging, timeline, composer); shell.append(sidebar, main); host.append(shell);
  const key = () => `${active.kind}:${active.id || ''}:${active.channel || ''}`;
  const draft = () => { if (!drafts.has(key())) drafts.set(key(), { text: '', files: [], nonce: nonce() }); return drafts.get(key()); };
  const request = async (operation, args = {}) => (await api('/api/admin', { operation, ...args })).result;

  function modal(title, build) {
    const trigger = document.activeElement;
    const dialog = el('dialog', undefined, 'confirm-dialog chat-dialog'); dialog.setAttribute('aria-label', title); dialog.append(el('h2', title));
    const close = () => { dialog.close(); dialog.remove(); if (trigger?.isConnected) trigger.focus(); };
    dialog.addEventListener('cancel', event => { event.preventDefault(); close(); });
    build(dialog, close); host.append(dialog); dialog.showModal(); return dialog;
  }
  async function open(selection) {
    shell.classList.remove('sidebar-open');
    active = selection; page = 1; query = ''; generation += 1;
    mode = selection.kind === 'history' ? 'announcement' : 'normal';
    recipients = { guilds: selection.kind === 'guild' ? [selection.id] : [], user: selection.kind === 'dm' ? selection.id : null,
      channel: selection.channel || null, all: false };
    timeline.replaceChildren(el('p', 'Loading conversation…', 'loading'));
    timeline.setAttribute('aria-busy', 'true');
    drawHeader(); drawComposer(); await refresh();
  }
  function drawSidebar() {
    const scroll = sidebar.scrollTop;
    const expanded = new Set([...sidebar.querySelectorAll('.chat-guild[open]')].map(item => item.dataset.guild));
    sidebar.replaceChildren(el('div', '◈ BOT MESSAGES', 'chat-brand'));
    sidebar.append(btn('Inbox', () => open({ kind: 'all', name: 'Inbox' })), btn('# announcements', () => open({ kind: 'history', name: 'Announcements' })));
    sidebar.append(btn('＋ New DM', () => modal('New direct message', (dialog, close) => {
      const input = el('input'); input.placeholder = 'Discord user ID'; input.setAttribute('aria-label', 'Discord user ID'); input.inputMode = 'numeric';
      dialog.append(input, btn('Open DM', async () => { const id = input.value.trim(); if (!/^\d{1,20}$/.test(id)) throw new Error('Use a copied Discord user ID.'); close(); await open({ kind: 'dm', id, name: 'User ' + id }); }));
    })));
    sidebar.append(el('h3', 'DIRECT MESSAGES', 'chat-section-label'));
    for (const thread of admin.threads.filter(item => item.kind === 'dm')) {
      const button = btn(thread.name, () => open({ kind: 'dm', id: thread.id, name: thread.name }), 'chat-thread');
      button.setAttribute('aria-label', thread.name);
      button.dataset.initial = thread.name.slice(0, 1).toUpperCase(); button.dataset.count = String(thread.count);
      button.classList.toggle('selected', active.kind === 'dm' && active.id === thread.id); button.title = `${thread.count} retained messages`; sidebar.append(button);
    }
    sidebar.append(el('h3', 'SERVERS & CHANNELS', 'chat-section-label'));
    for (const guild of admin.guilds) {
      const details = el('details', undefined, 'chat-guild'); details.dataset.guild = guild.id;
      details.open = expanded.has(guild.id) || active.kind === 'guild' && active.id === guild.id;
      details.append(el('summary', guild.name));
      let category;
      for (const channel of guild.channels || []) {
        if (category !== channel.category) { category = channel.category; if (category) details.append(el('small', category, 'chat-category')); }
        const button = btn(`${channel.thread ? '↳' : '#'} ${channel.name}`, () => open({ kind: 'guild', id: guild.id, channel: channel.id, name: channel.name, guild: guild.name }), 'chat-thread');
        button.disabled = !channel.sendable; button.title = channel.sendable ? 'Open channel conversation' : 'Bot cannot send here';
        button.classList.toggle('selected', active.channel === channel.id); details.append(button);
      }
      if (!(guild.channels || []).length) details.append(btn(`# ${guild.channel || 'inbox'}`, () => open({ kind: 'guild', id: guild.id,
        channel: guild.default_channel_id || null, name: guild.channel || guild.name, guild: guild.name }), 'chat-thread'));
      sidebar.append(details);
    }
    const themes = el('select'); themes.setAttribute('aria-label', 'Chat theme');
    for (const [value, label] of [['glass', 'Liquid glass'], ['midnight', 'Midnight'], ['amethyst', 'Amethyst'], ['light', 'Light']]) { const option = el('option', label); option.value = value; themes.append(option); }
    let theme = 'glass'; try { theme = localStorage.getItem('media-chat-theme') || 'glass'; } catch {}
    themes.value = ['glass', 'midnight', 'amethyst', 'light'].includes(theme) ? theme : 'glass'; shell.dataset.theme = themes.value;
    themes.addEventListener('change', () => { shell.dataset.theme = themes.value; try { localStorage.setItem('media-chat-theme', themes.value); } catch {} });
    sidebar.append(themes, el('small', `${admin.retention_days || 7} day message retention`, 'fine'));
    sidebar.scrollTop = scroll;
  }
  function drawHeader() {
    const toggle = btn('Conversations', () => {
      const expanded = shell.classList.toggle('sidebar-open'); toggle.setAttribute('aria-expanded', String(expanded));
    }, 'button subtle conversation-toggle');
    toggle.setAttribute('aria-controls', sidebar.id); toggle.setAttribute('aria-expanded', String(shell.classList.contains('sidebar-open')));
    header.replaceChildren(toggle, el('span', active.kind === 'dm' ? '◉' : '#', 'chat-header-icon'), el('h2', active.name));
    if (active.guild) header.append(el('span', active.guild, 'fine'));
    const search = el('form', undefined, 'chat-search');
    const input = el('input'); input.type = 'search'; input.maxLength = 100; input.value = query; input.placeholder = 'Search this conversation'; input.setAttribute('aria-label', 'Search conversation');
    search.append(input); search.hidden = active.kind === 'history';
    search.addEventListener('submit', async event => { event.preventDefault(); query = input.value.trim(); page = 1; generation += 1; await refresh(); });
    const status = el('span', 'Connecting…', 'chat-live'); status.id = 'chat-live';
    header.append(search, status, btn('Refresh', refresh));
  }
  function chooseRecipients() {
    modal('Announcement destinations', (dialog, close) => {
      const user = el('input'); user.placeholder = 'User ID · optional DM'; user.value = recipients.user || ''; user.setAttribute('aria-label', 'Announcement DM recipient');
      const checks = el('div', undefined, 'panel-recipients');
      for (const guild of admin.guilds) {
        const label = el('label'); const check = el('input'); check.type = 'checkbox'; check.value = guild.id; check.checked = recipients.guilds.includes(guild.id);
        label.append(check, el('span', guild.name)); checks.append(label);
      }
      const channel = el('select'); channel.setAttribute('aria-label', 'Announcement channel');
      const update = () => {
        const selected = [...checks.querySelectorAll('input:checked')].map(item => item.value);
        channel.replaceChildren(el('option', selected.length === 1 ? 'Choose a channel' : 'Configured default channels'));
        channel.options[0].value = '';
        if (selected.length === 1) for (const item of admin.guilds.find(g => g.id === selected[0]).channels || []) {
          const option = el('option', `${item.category ? item.category + ' / ' : ''}#${item.name}`); option.value = item.id; option.disabled = !item.sendable; channel.append(option);
        }
        channel.value = recipients.channel || ''; channel.disabled = selected.length !== 1;
      };
      checks.addEventListener('change', update); update();
      const label = el('label', undefined, 'check-row'); const all = el('input'); all.type = 'checkbox'; all.checked = recipients.all; all.disabled = !admin.all_users_enabled;
      label.append(all, el('span', 'Also DM all members (explicit broadcast)'));
      dialog.append(user, checks, channel, label, btn('Use destinations', () => {
        recipients = { user: user.value.trim() || null, guilds: [...checks.querySelectorAll('input:checked')].map(item => item.value), channel: channel.value || null, all: all.checked };
        close(); drawComposer();
      }, 'button primary'));
    });
  }
  function drawComposer() {
    composer.replaceChildren();
    if (active.kind === 'all') { composer.append(el('p', 'Open a DM or channel to reply, or use # announcements for a broadcast.', 'fine')); return; }
    const current = draft();
    const tools = el('div', undefined, 'chat-compose-tools');
    const select = el('select'); select.setAttribute('aria-label', 'Message mode');
    for (const [value, label] of [['normal', 'Normal chat'], ['announcement', 'Announcement']]) { const option = el('option', label); option.value = value; select.append(option); }
    select.value = mode; select.disabled = active.kind === 'history';
    select.addEventListener('change', () => { mode = select.value; drawComposer(); }); tools.append(select);
    if (mode === 'announcement') tools.append(btn('Choose destinations', chooseRecipients), el('span', recipients.user ? `DM ${recipients.user}` : `${recipients.guilds.length} servers${recipients.channel ? ' · selected channel' : ''}`, 'fine'));
    else tools.append(el('span', `Reply as the bot · ${active.name}`, 'fine'));
    const textarea = el('textarea', undefined, 'panel-draft'); textarea.maxLength = 2000; textarea.rows = 3; textarea.value = current.text;
    textarea.placeholder = `Message ${active.kind === 'dm' ? '@' : '#'}${active.name}`; textarea.setAttribute('aria-label', 'Message');
    const counter = el('small', `${current.text.length} / 2,000`, 'chat-compose-count'); counter.setAttribute('aria-label', 'Message character count');
    const resize = () => { textarea.style.height = 'auto'; textarea.style.height = `${Math.min(160, Math.max(64, textarea.scrollHeight))}px`; };
    textarea.addEventListener('input', () => { current.text = textarea.value; current.nonce = nonce(); counter.textContent = `${current.text.length} / 2,000`; resize(); });
    const attachments = el('div', undefined, 'chat-upload-list');
    const drawFiles = () => {
      attachments.replaceChildren(chatAttachments(current.files));
      for (const file of current.files) attachments.append(btn(`Remove ${file.filename}`, () => { current.files = current.files.filter(item => item.id !== file.id); current.nonce = nonce(); drawFiles(); }, 'chat-remove-file'));
    };
    drawFiles();
    const input = el('input'); input.type = 'file'; input.multiple = true; input.hidden = true;
    input.addEventListener('change', async () => {
      if (current.uploading) return;
      current.uploading = true;
      try {
        if (current.files.length + input.files.length > 4) throw new Error('Choose up to four files per message.');
        for (const file of input.files) {
          if (!file.size || file.size > 8 * 1024 * 1024) throw new Error('Each file is limited to 8 MiB.');
          const form = new FormData(); form.append('file', file);
          const result = await api('/api/admin/uploads', form);
          if (disposed || !host.isConnected) return;
          current.files.push({ ...result.result, upload: result.result.id }); current.nonce = nonce(); drawFiles();
        }
      } catch (error) { reportError(error.message); } finally { current.uploading = false; input.value = ''; }
    });
    let sending = false;
    const send = async () => {
      if (sending) return;
      if (current.uploading) throw new Error('Wait for attachments to finish uploading before sending.');
      current.text = textarea.value; // Also supports autofill/test inputs without an input event.
      if (!current.text.trim() && !current.files.length) throw new Error('Write a message or attach a file first.');
      sending = true;
      try {
        const uploads = current.files.map(file => file.id);
        const sentFiles = current.files.map(file => ({ ...file })), sentNonce = current.nonce;
        const clearSent = () => {
          if (current.nonce !== sentNonce) return;
          current.text = ''; current.files = []; current.nonce = nonce(); textarea.value = ''; counter.textContent = '0 / 2,000'; resize(); drawFiles();
        };
        if (mode === 'normal') {
          await request('chat', { message: current.text, uploads, request_id: current.nonce,
            user_id: active.kind === 'dm' ? active.id : null, guild_id: active.kind === 'guild' ? active.id : null, channel_id: active.channel || null });
          clearSent(); await refresh();
        } else {
          const plan = await request('prepare', { message: current.text, uploads, guild_ids: recipients.guilds,
            user_id: recipients.user, channel_id: recipients.channel, all_users: recipients.all });
          modal('Confirm announcement', (dialog, close) => {
            dialog.append(el('p', `${plan.channels} channels · ${plan.users} DMs · ${plan.skipped.length} unavailable`));
            for (const target of plan.destinations) dialog.append(el('p', target, 'discord-tag'));
            dialog.append(discordContent(plan.message), chatAttachments(sentFiles), el('p', 'Mentions and URL unfurls are disabled.', 'fine'));
            dialog.append(btn('Cancel', close), btn('Confirm', async () => { await request('send', { plan: plan.plan, confirmed: true }); close(); clearSent(); await refresh(); }, 'button primary'));
          });
        }
      } finally { sending = false; }
    };
    const sendButton = btn(mode === 'normal' ? 'Send' : 'Preview announcement', send, 'button primary');
    textarea.addEventListener('keydown', event => { if (event.key === 'Enter' && !event.shiftKey && !event.isComposing && mode === 'normal') { event.preventDefault(); sendButton.click(); } });
    tools.append(btn('＋ Attach files', () => input.click()), counter, sendButton);
    composer.append(attachments, textarea, tools, input, el('small', 'Enter to send · Shift+Enter for a new line · Up to 4 files, 8 MiB each / 16 MiB total', 'fine'));
    resize();
  }
  async function refresh() {
    if (disposed) return;
    if (refreshBusy) { refreshAgain = true; return; }
    refreshBusy = true;
    const version = generation;
    try {
      const [nextAdmin, result] = await Promise.all([request('state'), active.kind === 'history' ? request('history', { page }) : request('inbox', {
        kind: active.kind === 'all' ? 'all' : active.kind === 'dm' ? 'dm' : 'guild', guild_id: active.kind === 'guild' ? active.id : null,
        user_id: active.kind === 'dm' ? active.id : null, channel_id: active.channel || null, page, query, order: 'newest' })]);
      if (disposed || version !== generation) { refreshAgain = true; return; }
      admin = nextAdmin; drawSidebar();
      if (active.kind === 'dm') {
        const thread = admin.threads.find(item => item.kind === 'dm' && item.id === active.id);
        if (thread) { active.name = thread.name; header.querySelector('h2').textContent = thread.name; }
      }
      const nearBottom = timeline.scrollHeight - timeline.scrollTop - timeline.clientHeight < 100;
      const scroll = timeline.scrollTop;
      const messages = active.kind === 'history' ? [...result.jobs].reverse().map(job => ({ message_id: `job-${job.id}`, author_name: 'Media', content: job.content,
        created_at: job.created_at, mode: 'announcement', direction: 'out', status: job.counts?.uncertain || job.deliveries.some(d => d.status === 'uncertain') ? 'uncertain' :
          job.counts?.failed || job.deliveries.some(d => d.status === 'failed') ? 'failed' : job.counts?.pending || job.deliveries.some(d => d.status !== 'sent') ? 'pending' : 'sent',
        attachments: job.attachments, deliveries: job.deliveries, job: job.id, counts: job.counts })) : [...result.messages].reverse();
      const old = new Map([...timeline.children].map(node => [node.dataset.message, node]));
      const nodes = [];
      let lastDay;
      for (const item of messages) {
        const day = new Date(item.created_at * 1000).toLocaleDateString(undefined, { year: 'numeric', month: 'long', day: 'numeric' });
        if (day !== lastDay) { const divider = el('div', day, 'chat-date'); nodes.push(divider); lastDay = day; }
        const signature = JSON.stringify([key(), item]); let node = old.get(item.message_id);
        if (!node || node.dataset.signature !== signature) {
          node = chatMessage(item); node.dataset.signature = signature;
          const body = node.querySelector('.chat-message-body');
          if (item.deliveries) {
            const details = el('details', undefined, 'chat-delivery-details'); details.append(el('summary', `${item.counts?.total || item.deliveries.length} destinations · ${item.counts?.sent ?? ''} sent · delivery details`));
            for (const delivery of item.deliveries) details.append(el('p', `${delivery.label} · ${delivery.status}${delivery.error ? ' · ' + delivery.error : ''}`)); body.append(details);
            details.append(btn('Full delivery history', () => modal('Announcement delivery history', (dialog, close) => {
              let deliveryPage = 1;
              const rows = el('div'); const controls = el('div', undefined, 'chat-paging'); dialog.append(rows, controls);
              async function load() {
                const data = await request('deliveries', { job: item.job, page: deliveryPage });
                rows.replaceChildren(...data.rows.map(row => el('p', `${row.label} · ${row.status}${row.error ? ' · ' + row.error : ''}`)));
                const previous = btn('Previous', async () => { deliveryPage -= 1; await load(); }); previous.disabled = deliveryPage === 1;
                const next = btn('Next', async () => { deliveryPage += 1; await load(); }); next.disabled = deliveryPage * 5 >= data.total;
                controls.replaceChildren(previous, el('small', `Page ${deliveryPage}`), next, btn('Close', close));
              }
              void load().catch(error => reportError(error.message));
            })));
          } else if (active.kind === 'all') {
            body.append(el('small', item.guild_name ? `${item.guild_name} / #${item.channel_name}` : 'Direct message', 'fine'), btn('Reply', () => open(item.guild_id ?
              { kind: 'guild', id: item.guild_id, channel: item.channel_id, name: item.channel_name, guild: item.guild_name } :
              { kind: 'dm', id: item.peer_id || item.author_id, name: item.peer_name || item.author_name })));
          }
        }
        nodes.push(node);
      }
      timeline.replaceChildren(...nodes);
      if (!messages.length) timeline.append(el('p', active.kind === 'history' ? 'No announcements yet. Compose one below.' : 'This is the start of the retained conversation. Replies appear here as the bot.', 'chat-empty'));
      timeline.scrollTop = nearBottom ? timeline.scrollHeight : scroll;
      const older = btn('Older messages', async () => { page += 1; generation += 1; await refresh(); }); older.disabled = page * (active.kind === 'history' ? 25 : 50) >= result.total;
      const newer = btn('Newer messages', async () => { page = Math.max(1, page - 1); generation += 1; await refresh(); }); newer.disabled = page === 1;
      paging.replaceChildren(older, el('small', `Page ${page} · ${result.total} retained`, 'fine'), newer);
      const status = header.querySelector('.chat-live'); if (status) status.textContent = source?.readyState === 1 ? '● Live' : '● Reconnecting';
    } catch (error) {
      if (!disposed) {
        reportError(error.message);
        if (version === generation && timeline.querySelector('.loading')) timeline.replaceChildren(el('p', 'Conversation unavailable. Use Refresh to try again.', 'chat-empty'));
      }
    } finally {
      if (version === generation) timeline.removeAttribute('aria-busy');
      refreshBusy = false; if (refreshAgain && !disposed) { refreshAgain = false; void refresh(); }
    }
  }
  admin = await request('state');
  if (disposed || !host.isConnected) return { dispose() {} };
  drawSidebar(); drawHeader(); drawComposer(); await refresh();
  if (!host.isConnected) { shell.remove(); drafts.clear(); return { dispose() {} }; }
  if (typeof EventSource === 'function') {
    source = new EventSource('/api/admin/events', { withCredentials: true });
    source.addEventListener('update', () => void refresh());
    source.addEventListener('error', async () => {
      if (disposed) return;
      const status = header.querySelector('.chat-live'); if (status) status.textContent = '● Reconnecting';
      try { await api('/api/state'); }
      catch (error) {
        if (error.status === 401) source.close();
        else if (!disposed) reportError('Live updates disconnected. Reconnecting; check the SSH tunnel if this continues.');
      }
    });
    source.addEventListener('open', () => { const status = header.querySelector('.chat-live'); if (status) status.textContent = '● Live'; });
  }
  return { dispose() { disposed = true; generation += 1; source?.close(); drafts.clear(); shell.remove(); host.querySelectorAll('.chat-dialog').forEach(dialog => dialog.remove()); } };
}
