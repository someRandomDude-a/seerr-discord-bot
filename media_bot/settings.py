"""Private, atomic configuration persistence. Environment values override saved settings."""
import json
import os
import tempfile
from pathlib import Path


SECRET_KEYS = frozenset(('DISCORD_TOKEN', 'SEERR_ADMIN_KEY', 'DISCORD_CLIENT_SECRET', 'WEBHOOK_SECRET',
                         'LIDARR_API_KEY', 'READARR_API_KEY', 'RADARR_API_KEY', 'SONARR_API_KEY'))


def settings_path():
    return Path(os.getenv('DATA_DIR', './data')) / 'settings.json'


def load_settings(path=None):
    path = Path(path) if path else settings_path()
    if not path.exists():
        return {}
    data = json.loads(path.read_text(encoding='utf-8'))
    if not isinstance(data, dict) or data.get('version') != 1 or not isinstance(data.get('settings'), dict):
        raise ValueError('Invalid saved settings; restore a backup instead of overwriting this file.')
    if any(not isinstance(k, str) or not isinstance(v, str) for k, v in data['settings'].items()):
        raise ValueError('Saved setting values must be strings')
    return data['settings']


def save_settings(values, path=None):
    path = Path(path) if path else settings_path()
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, name = tempfile.mkstemp(prefix='.settings-', dir=path.parent)
    try:
        with os.fdopen(fd, 'w', encoding='utf-8') as stream:
            os.chmod(name, 0o600)
            json.dump({'version': 1, 'settings': values}, stream, indent=2)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(name, path)
    finally:
        if os.path.exists(name):
            os.unlink(name)


def effective_settings():
    return {**load_settings(), **os.environ}
