// Discord-like formatting without HTML injection, remote tracking images, or executable links.
function element(tag, text, cls) {
  const el = document.createElement(tag);
  if (text !== undefined) el.textContent = String(text);
  if (cls) el.className = cls;
  return el;
}

export function safeLink(value) {
  try {
    const url = new URL(value);
    return ['https:', 'http:'].includes(url.protocol) && !url.username && !url.password ? url.href : null;
  } catch { return null; }
}

export function discordContent(text = '', tags = {}, depth = 0) {
  text = String(text);
  const result = element('span', undefined, 'discord-content');
  if (depth > 4) { result.textContent = text; return result; }
  if (depth === 0 && !text.includes('```') && /^(?:> |#{1,3} |- )/m.test(text)) {
    for (const line of text.split('\n')) {
      const prefix = /^(> |#{1,3} |- )/.exec(line)?.[0];
      const row = element(prefix?.startsWith('>') ? 'blockquote' : prefix?.startsWith('#') ? `h${prefix.trim().length}` : 'div');
      row.append(discordContent(prefix ? line.slice(prefix.length) : line, tags, depth + 1));
      if (prefix === '- ') row.className = 'discord-list-item';
      result.append(row);
    }
    return result;
  }
  const pattern = /```(?:[^\n`]*\n)?([\s\S]*?)```|`([^`\n]+)`|<(@[!&]?|#)(\d+)>|<(a?):([A-Za-z0-9_]+):(\d+)>|<t:(\d+)(?::([tTdDfFR]))?>|\[([^\]\n]+)\]\(([^\s)]+)\)|(https?:\/\/[^\s<>]+)|(@everyone|@here)|\*\*([^\n]+?)\*\*|__([^\n]+?)__|~~([^\n]+?)~~|\|\|([\s\S]+?)\|\||\*([^*\n]+)\*/g;
  let cursor = 0;
  for (const match of String(text).matchAll(pattern)) {
    result.append(document.createTextNode(text.slice(cursor, match.index)));
    const [, block, code, marker, id, animated, emoji, emojiID, seconds, style, label, href, plainURL, everyone, bold, underline, strike, spoiler, italic] = match;
    let part;
    if (block !== undefined) part = element('pre', block, 'discord-code');
    else if (code) part = element('code', code);
    else if (marker) {
      const kind = marker === '#' ? 'channel' : marker === '@&' ? 'role' : 'user';
      part = element('span', `${kind === 'channel' ? '#' : '@'}${tags[`${kind}:${id}`] || `${kind} ${id}`}`, 'discord-tag');
      part.title = id;
    } else if (emoji) {
      if (tags.__message && tags.__emoji?.includes(emojiID)) {
        part = element('img', undefined, 'discord-emoji'); part.alt = `:${emoji}:`; part.loading = 'lazy';
        part.src = `/api/admin/media/${encodeURIComponent(tags.__message)}/emoji-${encodeURIComponent(emojiID)}`;
      } else part = element('span', `:${emoji}:`, 'discord-emoji');
      part.title = `Discord emoji ${emojiID}`;
    }
    else if (seconds) {
      const date = new Date(Number(seconds) * 1000);
      let display = date.toLocaleString();
      if (Number.isFinite(date.getTime()) && style === 'R') {
        const delta = Number(seconds) - Date.now() / 1000;
        const [unit, divisor] = Math.abs(delta) >= 86400 ? ['day', 86400] : Math.abs(delta) >= 3600 ? ['hour', 3600] : Math.abs(delta) >= 60 ? ['minute', 60] : ['second', 1];
        display = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' }).format(Math.round(delta / divisor), unit);
      } else if (['t', 'T'].includes(style)) display = date.toLocaleTimeString();
      else if (['d', 'D'].includes(style)) display = date.toLocaleDateString();
      part = element('time', Number.isFinite(date.getTime()) ? display : match[0], 'discord-timestamp');
      part.title = style === 'R' ? 'Discord relative timestamp' : 'Discord timestamp';
    } else if (href || plainURL) {
      const url = safeLink(href || plainURL);
      part = element(url ? 'a' : 'span', label || plainURL || match[0]);
      if (url) { part.href = url; part.target = '_blank'; part.rel = 'noopener noreferrer'; }
    } else if (everyone) part = element('span', everyone, 'discord-tag');
    else {
      part = element(spoiler ? 'button' : bold ? 'strong' : underline ? 'u' : strike ? 's' : 'em');
      part.append(discordContent(bold || underline || strike || spoiler || italic, tags, depth + 1));
      if (spoiler) {
        part.type = 'button'; part.className = 'discord-spoiler'; part.setAttribute('aria-expanded', 'false');
        part.setAttribute('aria-label', 'Reveal spoiler');
        part.addEventListener('click', () => part.setAttribute('aria-expanded', part.getAttribute('aria-expanded') !== 'true' ? 'true' : 'false'));
      }
    }
    result.append(part); cursor = match.index + match[0].length;
  }
  result.append(document.createTextNode(String(text).slice(cursor)));
  return result;
}

export function chatAttachments(items = [], messageID) {
  const grid = element('div', undefined, 'chat-attachments');
  for (const item of items) {
    const url = item.upload ? '/api/admin/files/' + encodeURIComponent(item.upload) : item.key && messageID ?
      `/api/admin/media/${encodeURIComponent(messageID)}/${encodeURIComponent(item.key)}` : null;
    if (!url) continue;
    const card = element('a', undefined, 'chat-file'); card.href = url; card.target = '_blank'; card.rel = 'noopener noreferrer';
    if (item.image) {
      const image = element('img'); image.src = url; image.alt = item.filename || 'Image attachment'; image.loading = 'lazy';
      card.append(image);
    }
    card.append(element('span', item.filename || 'Attachment'), element('small', `${Math.ceil((item.size || 0) / 1024)} KiB`));
    grid.append(card);
  }
  return grid;
}

export function chatMessage(item) {
  const row = element('article', undefined, 'chat-message'); row.dataset.message = item.message_id;
  const avatar = element('div', (item.author_name || 'M').slice(0, 1).toUpperCase(), 'chat-avatar');
  if (item.avatar) {
    const image = element('img'); image.src = `/api/admin/media/${encodeURIComponent(item.message_id)}/avatar`; image.alt = ''; image.loading = 'lazy'; avatar.replaceChildren(image);
  }
  const body = element('div', undefined, 'chat-message-body');
  const meta = element('div', undefined, 'chat-meta');
  meta.append(element('strong', item.author_name || 'Media'), element('time', new Date(item.created_at * 1000).toLocaleString()));
  if (item.bot || item.direction === 'out') meta.append(element('span', 'BOT', 'chat-badge'));
  if (item.mode === 'announcement') meta.append(element('span', 'ANNOUNCEMENT', 'chat-badge announcement'));
  if (item.edited_at) meta.append(element('small', '(edited)'));
  if (item.direction === 'out') meta.append(element('span', item.status || 'sent', `chat-delivery ${item.status || 'sent'}`));
  body.append(meta);
  const content = element('div', undefined, 'received-content');
  const tags = { ...item.tags, __message: item.message_id, __emoji: item.emoji_ids || [] };
  content.append(discordContent(item.deleted ? 'Message deleted' : item.content || '', tags)); body.append(content);
  if (!item.deleted) {
    body.append(chatAttachments(item.attachments, item.message_id));
    for (const embed of item.embeds || []) {
      const card = element('section', undefined, 'chat-embed');
      if (embed.title) card.append(element('strong', embed.title));
      if (embed.description) card.append(discordContent(embed.description, item.tags));
      for (const field of embed.fields || []) {
        const part = element('div', undefined, 'chat-embed-field');
        part.append(element('strong', field.name), discordContent(field.value || '', item.tags)); card.append(part);
      }
      for (const key of [embed.image, embed.thumbnail].filter(Boolean)) {
        const image = element('img'); image.src = `/api/admin/media/${encodeURIComponent(item.message_id)}/${encodeURIComponent(key)}`; image.alt = embed.title || 'Embed image'; image.loading = 'lazy'; card.append(image);
      }
      if (embed.footer) card.append(element('small', embed.footer));
      body.append(card);
    }
  }
  row.append(avatar, body); return row;
}
