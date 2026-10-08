"""Bounded operator attachments and untrusted Discord display metadata."""
import json
import re
import secrets
import time
from urllib.parse import urlsplit

from aiohttp import ClientSession, ClientTimeout
from .security import UserError

FILE_LIMIT = 8 * 1024 * 1024
TOTAL_LIMIT = 16 * 1024 * 1024


def image_type(body):
    if body.startswith(b'\x89PNG\r\n\x1a\n'):
        return 'image/png'
    if body.startswith(b'\xff\xd8\xff'):
        return 'image/jpeg'
    if body.startswith((b'GIF87a', b'GIF89a')):
        return 'image/gif'
    if body.startswith(b'RIFF') and body[8:12] == b'WEBP':
        return 'image/webp'
    return 'application/octet-stream'


def cdn_url(value):
    if not isinstance(value, str) or len(value) > 4096:
        return None
    try:
        url = urlsplit(value)
    except ValueError:
        return None
    if (url.scheme != 'https' or url.netloc not in ('cdn.discordapp.com', 'media.discordapp.net') or
        not re.fullmatch(r'/(?:attachments|avatars|embed/avatars|emojis)/[A-Za-z0-9_./-]+', url.path) or
        '..' in url.path or url.fragment):
        return None
    return value


def metadata(message):
    assets, attachments, embeds, tags = {}, [], [], {}
    avatar = getattr(getattr(message.author, 'display_avatar', None), 'url', None)
    if cdn_url(avatar):
        assets['avatar'] = {'url': avatar, 'filename': 'avatar', 'image': True}
    for user in getattr(message, 'mentions', []):
        tags['user:' + str(user.id)] = str(getattr(user, 'display_name', None) or user)
    for role in getattr(message, 'role_mentions', []):
        tags['role:' + str(role.id)] = role.name
    for channel in getattr(message, 'channel_mentions', []):
        tags['channel:' + str(channel.id)] = channel.name
    emoji_ids = []
    for animated, name, eid in re.findall(r'<(a?):([A-Za-z0-9_]+):(\d+)>', message.content or '')[:10]:
        key = 'emoji-' + eid
        assets[key] = {'url': f'https://cdn.discordapp.com/emojis/{eid}.{"gif" if animated else "png"}', 'filename': name, 'image': True}
        emoji_ids.append(eid)
    for index, attachment in enumerate(message.attachments[:10]):
        key = 'attachment' + str(index)
        url = cdn_url(attachment.url)
        item = {'key': key, 'filename': str(attachment.filename)[:160], 'size': attachment.size,
            'image': str(attachment.content_type or '') in ('image/png', 'image/jpeg', 'image/gif', 'image/webp')}
        if url:
            assets[key] = {'url': url, **item}
            attachments.append(item)
    for index, embed in enumerate(getattr(message, 'embeds', [])[:10]):
        raw = embed.to_dict()
        item = {key: raw[key] for key in ('title', 'description', 'color', 'url') if key in raw}
        item['fields'] = [{key: field.get(key) for key in ('name', 'value', 'inline')} for field in raw.get('fields', [])[:25]]
        item['footer'] = (raw.get('footer') or {}).get('text', '')
        for slot in ('image', 'thumbnail'):
            url = cdn_url((raw.get(slot) or {}).get('proxy_url') or (raw.get(slot) or {}).get('url'))
            if url:
                key = f'embed{index}{slot}'
                assets[key] = {'url': url, 'filename': slot, 'image': True}
                item[slot] = key
        embeds.append(item)
    return {'attachments': attachments, 'embeds': embeds, 'tags': tags, 'assets': assets,
        'avatar': 'avatar' if 'avatar' in assets else None, 'emoji_ids': emoji_ids, 'bot': bool(getattr(message.author, 'bot', False))}


def public_message(row):
    result = dict(row)
    rich = json.loads(result.pop('rich', '{}'))
    rich.pop('assets', None)  # Signed CDN URLs stay server-side, behind authenticated routes.
    result.update(rich)
    return result


class ChatFiles:
    def __init__(self, store):
        self.store = store

    def prune(self):
        with self.store.connect() as db:
            db.execute('DELETE FROM admin_files WHERE expires<?', (time.time(),))

    def upload(self, actor, filename, body):
        if not body or len(body) > FILE_LIMIT:
            raise UserError('Each file must contain 1 byte to 8 MiB.')
        filename = re.sub(r'[\x00-\x1f\x7f]', '', str(filename).replace('\\', '/').rsplit('/', 1)[-1])[:120] or 'file'
        self.prune()
        key = secrets.token_urlsafe(24)
        with self.store.connect() as db:
            db.execute('BEGIN IMMEDIATE')
            total = db.execute('SELECT COALESCE(SUM(LENGTH(body)),0) FROM admin_files').fetchone()[0]
            if total + len(body) > 64 * 1024 * 1024:
                raise UserError('Attachment storage is full (64 MiB). Wait for retention expiry before uploading more.')
            db.execute('INSERT INTO admin_files VALUES (?,?,?,?,?,?,NULL)',
                (key, str(actor), filename, image_type(body), body, time.time() + 900))
        return {'id': key, 'filename': filename, 'size': len(body), 'image': image_type(body).startswith('image/')}

    def resolve(self, actor, ids, claimed=False):
        if not isinstance(ids, list) or len(ids) > 4 or any(not isinstance(key, str) for key in ids) or len(set(ids)) != len(ids):
            raise UserError('Choose up to four uploaded files.')
        rows = []
        with self.store.connect() as db:
            for key in ids:
                row = db.execute('SELECT * FROM admin_files WHERE id=? AND actor=? AND expires>?', (key, str(actor), time.time())).fetchone()
                if not row or not claimed and row['announcement_id'] is not None:
                    raise UserError('An upload expired, was already sent, or belongs to another operator. Upload again.')
                rows.append(dict(row))
        if sum(len(row['body']) for row in rows) > TOTAL_LIMIT:
            raise UserError('Attachments must total no more than 16 MiB.')
        return rows


async def fetch_asset(url):
    if not cdn_url(url):
        raise UserError('Only retained Discord CDN media may be fetched.')
    async with ClientSession(timeout=ClientTimeout(total=10)) as client:
        async with client.get(url, allow_redirects=False) as response:
            if response.status != 200:
                raise UserError('This Discord attachment expired or is unavailable. Refresh the conversation.')
            chunks, size = [], 0
            async for chunk in response.content.iter_chunked(64 * 1024):
                size += len(chunk)
                if size > FILE_LIMIT:
                    raise UserError('Inline/download preview is limited to 8 MiB.')
                chunks.append(chunk)
    return b''.join(chunks)
