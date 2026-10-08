"""User-token Jellyfin access. Never substitute Seerr's Jellyfin administrator API key."""
import re
from dataclasses import dataclass
from urllib.parse import quote

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
        authorization = f'MediaBrowser Client="Discord Media", Device="Discord", DeviceId="{quote(self.device_id, safe="")}", Version="1.0"'
        if self.token:
            # Jellyfin 10.11 can disable legacy X-Emby-Token authentication.
            # Use the native header for the retained *user* token, never an API key.
            # Jellyfin URL-decodes quoted header values; escaping prevents delimiters
            # or control characters from changing the authorization parameters.
            authorization += f', Token="{quote(self.token, safe="")}"'
        headers = {'Authorization': authorization}
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
