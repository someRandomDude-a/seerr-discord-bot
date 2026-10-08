"""User-token Jellyfin access. Never substitute Seerr's Jellyfin administrator API key."""
import re
from dataclasses import dataclass

import requests
from .config import http_url
from .security import UserError, VerificationRequired


def identity(value):
    if not isinstance(value, str) or not re.fullmatch(r'(?:[A-Fa-f0-9]{32}|[A-Fa-f0-9]{8}-(?:[A-Fa-f0-9]{4}-){3}[A-Fa-f0-9]{12})', value):
        raise ValueError('Invalid Jellyfin identity')
    return value.replace('-', '').lower()


@dataclass
class JellyfinConfig:
    url: str
    external_url: str
    server_id: str


def discover_jellyfin(api):
    row = api._request('GET', '/settings/jellyfin')
    if not isinstance(row, dict) or not row.get('ip') or not row.get('serverId'):
        raise ValueError('Jellyfin is not configured in Seerr')
    host = row['ip']
    if ':' in host and not host.startswith('['):
        host = '[' + host + ']'
    port = int(row.get('port', 8096))
    if not 0 < port <= 65535:
        raise ValueError('Invalid Jellyfin port')
    url = http_url(f"{'https' if row.get('useSsl') else 'http'}://{host}:{port}/" + (row.get('urlBase') or '').lstrip('/'), 'Jellyfin')
    external = http_url(row.get('externalHostname') or url, 'Jellyfin external URL')
    if external.endswith('/web/index.html'):
        external = external[:-15]
    elif external.endswith('/web'):
        external = external[:-4]
    return JellyfinConfig(url, external.rstrip('/'), identity(row['serverId']))


class JellyfinClient:
    def __init__(self, url, device_id, timeout=15, token=None):
        self.url, self.device_id, self.timeout, self.token = url, device_id, timeout, token

    def request(self, method, path, **kwargs):
        headers = {'Authorization': f'MediaBrowser Client="Discord Media", Device="Discord", DeviceId="{self.device_id}", Version="1.0"'}
        if self.token:
            headers['X-Emby-Token'] = self.token
        try:
            response = requests.request(method, self.url + path, headers=headers,
                timeout=self.timeout, allow_redirects=False, **kwargs)
            if response.status_code == 401 or path == '/Users/Me' and response.status_code in (400, 403, 404):
                raise VerificationRequired('Verification expired or was revoked. Run /link again.')
            if response.status_code in (403, 404):
                raise UserError('The item is no longer accessible.')
            if response.status_code != 200:
                raise UserError('Identity verification is unavailable. Try again later.')
            return response.json()
        except (requests.RequestException, ValueError):
            raise UserError('Identity verification is unavailable. Try again later.') from None

    def me(self):
        return self.request('GET', '/Users/Me')
