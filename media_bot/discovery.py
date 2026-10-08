"""Read Seerr-managed service configuration; never return keys to the browser."""
from .config import ArrConfig, http_url


def discover_arr(api):
    configs = {}
    for name in ('radarr', 'sonarr'):
        rows = api._request('GET', '/settings/' + name)
        if not isinstance(rows, list):
            raise ValueError('Invalid service settings from Seerr')
        for row in rows:
            hostname = row['hostname']
            if ':' in hostname and not hostname.startswith('['):
                hostname = '[' + hostname + ']'
            scheme = 'https' if row.get('useSsl') else 'http'
            port = int(row['port'])
            if not 0 < port <= 65535 or int(row['id']) < 0:
                raise ValueError('Invalid Seerr service port or instance ID')
            base = row.get('baseUrl') or ''
            url = http_url(f'{scheme}://{hostname}:{port}/' + base.lstrip('/'), name)
            if not row.get('apiKey'):
                raise ValueError('Missing API key in Seerr service configuration')
            identity = f"{name}:{int(row['id'])}"
            if identity in configs:
                raise ValueError('Duplicate Seerr service instance')
            configs[identity] = ArrConfig(name, url, row['apiKey'])
    return configs
