import json
import os
from dataclasses import dataclass, field
from pathlib import Path
from urllib.parse import urlsplit
from .settings import effective_settings


def http_url(value, name):
    if not isinstance(value, str):
        raise ValueError(f'{name} must be a URL string')
    parts = urlsplit(value)
    if parts.scheme not in ('http', 'https') or not parts.hostname or parts.username or parts.password:
        raise ValueError(f'{name} must be an HTTP(S) URL without credentials')
    if parts.query or parts.fragment:
        raise ValueError(f'{name} cannot contain a query or fragment')
    try:
        parts.port
    except ValueError:
        raise ValueError(f'{name} has an invalid port') from None
    return value.rstrip('/')


def integer(name, default, minimum=1, values=None):
    value = int((os.environ if values is None else values).get(name, str(default)))
    if value < minimum:
        raise ValueError(f'{name} must be at least {minimum}')
    return value


def boolean(name, default=False, values=None):
    value = (os.environ if values is None else values).get(name, str(default)).strip().lower()
    if value not in ('true', 'false'):
        raise ValueError(f'{name} must be true or false')
    return value == 'true'


def ids(name, values=None):
    items = [x.strip() for x in (os.environ if values is None else values).get(name, '').split(',') if x.strip()]
    if any(not x.isascii() or not x.isdigit() or not 0 < int(x) < 2 ** 64 for x in items):
        raise ValueError(f'{name} must contain comma-separated positive Discord IDs')
    return frozenset(int(x) for x in items)


@dataclass
class ArrConfig:
    name: str
    url: str
    key: str = field(repr=False)
    root: str = ''
    quality: int = 0
    metadata: int = 0


@dataclass
class Config:
    seerr_url: str
    seerr_key: str = field(repr=False)
    discord_token: str = field(repr=False)
    database: str
    webhook_public_url: str = ''
    host: str = '127.0.0.1'
    port: int = 8080
    sync_interval: int = 300
    timeout: int = 15
    link_ttl: int = 300
    link_poll_interval: int = 5
    rate_limit: int = 15
    rate_window: int = 60
    login_limit: int = 5
    login_window: int = 600
    webhook_secret: str = field(default='', repr=False)
    webhook_limit: int = 60
    guild_ids: frozenset = frozenset()
    arr_daily_limit: int = 10
    application_id: str = ''
    client_secret: str = field(default='', repr=False)
    activity_enabled: bool = False
    activity_session_ttl: int = 900
    activity_refresh_interval: int = 60
    admin_ids: frozenset = frozenset()
    announcement_channels: dict = field(default_factory=dict)
    members_intent: bool = False
    inbox_message_content: bool = False
    inbox_channel_ids: frozenset = frozenset()
    inbox_retention_days: int = 7
    inbox_max_messages: int = 10000
    admin_ui_rate_limit: int = 120
    admin_max_recipients: int = 1000
    admin_send_limit: int = 5
    admin_send_window: int = 300
    admin_send_interval: int = 1
    devices: dict = field(default_factory=dict)
    arr: dict = field(default_factory=dict)
    seerr_discovery: bool = True
    seerr_admins: bool = True

    @classmethod
    def from_env(cls):
        return cls.from_values(effective_settings())

    @classmethod
    def from_values(cls, values):
        get = values.get
        num = lambda name, default, minimum=1: integer(name, default, minimum, values)
        flag = lambda name: boolean(name, values=values)
        id_set = lambda name: ids(name, values)
        for name in ('SEERR_URL', 'SEERR_ADMIN_KEY', 'DISCORD_TOKEN'):
            if not get(name):
                raise ValueError(f'{name} is required')
        public = get('WEBHOOK_PUBLIC_URL', '')
        if public:
            public = http_url(public, 'WEBHOOK_PUBLIC_URL')
        data = Path(get('DATA_DIR', './data'))
        data.mkdir(parents=True, exist_ok=True)
        database = Path(get('DATABASE_PATH', 'seerr_cache.db'))
        if not database.is_absolute():
            database = data / database
        database.parent.mkdir(parents=True, exist_ok=True)
        devices = json.loads(get('JELLYFIN_DEVICE_URLS', '{}'))
        if not isinstance(devices, dict) or len(devices) > 20:
            raise ValueError('JELLYFIN_DEVICE_URLS must be an object with at most 20 devices')
        for name, url in devices.items():
            if not isinstance(name, str) or not 1 <= len(name) <= 80:
                raise ValueError('Invalid device name')
            http_url(url, 'JELLYFIN_DEVICE_URLS')
        arr = {}
        discovery = boolean('SEERR_DISCOVERY', True, values)
        for name in ('radarr', 'sonarr', 'lidarr', 'readarr'):
            if discovery and name in ('radarr', 'sonarr'):
                continue  # Seerr is authoritative; unused legacy environment pairs cannot block discovery.
            prefix = name.upper()
            url, key = get(f'{prefix}_URL', ''), get(f'{prefix}_API_KEY', '')
            if bool(url) != bool(key):
                raise ValueError(f'{prefix}_URL and {prefix}_API_KEY must be configured together')
            if url:
                arr[name] = ArrConfig(name, http_url(url, f'{prefix}_URL'), key,
                    get(f'{prefix}_ROOT_FOLDER', ''), num(f'{prefix}_QUALITY_PROFILE_ID', 0, 0),
                    num(f'{prefix}_METADATA_PROFILE_ID', 0, 0))
        secret = get('WEBHOOK_SECRET', '')
        if secret and len(secret) < 32:
            raise ValueError('WEBHOOK_SECRET must be at least 32 characters')
        activity = flag('ACTIVITY_ENABLED')
        app_id, client_secret = get('DISCORD_APPLICATION_ID', ''), get('DISCORD_CLIENT_SECRET', '')
        if activity and (not app_id.isdigit() or not client_secret):
            raise ValueError('Activity requires DISCORD_APPLICATION_ID and DISCORD_CLIENT_SECRET')
        channels = json.loads(get('ADMIN_ANNOUNCEMENT_CHANNELS', '{}'))
        if not isinstance(channels, dict) or any(not str(g).isascii() or not str(g).isdigit() or
            not str(c).isascii() or not str(c).isdigit() or not 0 < int(g) < 2 ** 64 or not 0 < int(c) < 2 ** 64 for g, c in channels.items()):
            raise ValueError('ADMIN_ANNOUNCEMENT_CHANNELS must map guild IDs to channel IDs')
        port = num('BOT_PORT', 8080)
        if port > 65535:
            raise ValueError('BOT_PORT must be between 1 and 65535')
        return cls(http_url(values['SEERR_URL'], 'SEERR_URL'), values['SEERR_ADMIN_KEY'],
                   values['DISCORD_TOKEN'], str(database), public,
                   host=get('BOT_HOST', '127.0.0.1'), port=port,
                   sync_interval=num('SYNC_INTERVAL_SECONDS', 300), timeout=num('API_TIMEOUT_SECONDS', 15),
                   link_ttl=min(300, num('LINK_TTL_SECONDS', 300)), rate_limit=num('RATE_LIMIT_COUNT', 15),
                   link_poll_interval=num('LINK_POLL_INTERVAL_SECONDS', 5),
                   rate_window=num('RATE_LIMIT_WINDOW_SECONDS', 60), login_limit=num('LOGIN_RATE_LIMIT_COUNT', 5),
                   login_window=num('LOGIN_RATE_LIMIT_WINDOW_SECONDS', 600), webhook_secret=secret,
                   webhook_limit=num('WEBHOOK_RATE_LIMIT_COUNT', 60),
                   guild_ids=id_set('ALLOWED_GUILD_IDS'),
                   arr_daily_limit=num('ARR_REQUEST_LIMIT_PER_DAY', 10),
                   application_id=app_id, client_secret=client_secret, activity_enabled=activity,
                   activity_session_ttl=num('ACTIVITY_SESSION_TTL_SECONDS', 900),
                   activity_refresh_interval=num('ACTIVITY_REFRESH_INTERVAL_SECONDS', 60, 30),
                   admin_ids=id_set('ADMIN_DISCORD_IDS'),
                   announcement_channels={int(g): int(c) for g, c in channels.items()},
                   members_intent=flag('ENABLE_MEMBERS_INTENT'),
                   inbox_message_content=flag('INBOX_MESSAGE_CONTENT'),
                   inbox_channel_ids=id_set('INBOX_CHANNEL_IDS'),
                   inbox_retention_days=num('INBOX_RETENTION_DAYS', 7),
                   inbox_max_messages=num('INBOX_MAX_MESSAGES', 10000),
                   admin_ui_rate_limit=num('ADMIN_UI_RATE_LIMIT_COUNT', 120),
                   admin_max_recipients=num('ADMIN_MAX_RECIPIENTS', 1000),
                   admin_send_limit=num('ADMIN_SEND_RATE_LIMIT_COUNT', 5),
                   admin_send_window=num('ADMIN_SEND_RATE_LIMIT_WINDOW_SECONDS', 300),
                   admin_send_interval=num('ADMIN_SEND_INTERVAL_SECONDS', 1),
                   devices=devices, arr=arr,
                   seerr_discovery=discovery,
                   seerr_admins=boolean('SEERR_ADMINS', True, values))
