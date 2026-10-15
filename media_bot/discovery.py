"""Read Seerr-managed service configuration; never return keys to the browser."""
from .config import ArrConfig, http_url
from seerr.concurrency import parallel_api


def arr_config(name, row):
    hostname = row['hostname']
    if ':' in hostname and not hostname.startswith('['):
        hostname = '[' + hostname + ']'
    scheme = 'https' if row.get('useSsl') else 'http'
    port = int(row['port'])
    if not 0 < port <= 65535 or isinstance(row['id'], bool) or int(row['id']) < 0:
        raise ValueError('Invalid Seerr service port or instance ID')
    base = row.get('baseUrl') or ''
    url = http_url(f'{scheme}://{hostname}:{port}/' + base.lstrip('/'), name)
    if not row.get('apiKey'):
        raise ValueError('Missing API key in Seerr service configuration')
    return ArrConfig(name, url, row['apiKey'])


def deletion_service(api, kind, media, is4k):
    """Resolve only the Seerr deletion destination, never every matching library."""
    if kind not in ('movie', 'tv'):
        raise ValueError('Unsupported deletion kind')
    name = 'radarr' if kind == 'movie' else 'sonarr'
    rows = api._request('GET', '/settings/' + name)
    if not isinstance(rows, list) or any(not isinstance(row, dict) for row in rows):
        raise ValueError('Invalid service settings from Seerr')
    configs = {int(row['id']): row for row in rows}
    if len(configs) != len(rows):
        raise ValueError('Duplicate Seerr service instance')
    defaults = [row for row in rows if row.get('isDefault') is True and row.get('is4k', False) is is4k]
    service_id = media.get('serviceId4k' if is4k else 'serviceId')
    if service_id is not None:
        if isinstance(service_id, bool) or not isinstance(service_id, int) or service_id < 0:
            raise ValueError('Invalid media service instance')
        selected = configs.get(service_id)
        # Older Seerr releases treat instance zero as falsy when choosing the
        # file-deletion destination. Never risk deleting in a different default.
        if service_id == 0 and (len(defaults) != 1 or defaults[0]['id'] != 0):
            raise ValueError('Ambiguous Seerr instance-zero deletion destination')
    else:
        selected = defaults[0] if len(defaults) == 1 else None
    if not selected or selected.get('is4k', False) is not is4k:
        raise ValueError('No unique service configured for this deletion edition')
    provider_id = media.get('tmdbId' if kind == 'movie' else 'tvdbId')
    if kind == 'tv':
        # Seerr's file endpoint prefers TMDB's live TVDB mapping to stored media
        # metadata. Conflicting identities must never select two different shows.
        live_id = (api.get_tv_details(media['tmdbId']).get('externalIds') or {}).get('tvdbId')
        if live_id is not None:
            if provider_id is not None and provider_id != live_id:
                raise ValueError('Conflicting TVDB deletion identities')
            provider_id = live_id
    if isinstance(provider_id, bool) or not isinstance(provider_id, int) or provider_id <= 0:
        raise ValueError('Missing deletion provider identity')
    return f"{name}:{int(selected['id'])}", arr_config(name, selected), provider_id


def discover_arr(api):
    configs = {}
    names = ('radarr', 'sonarr')
    results = parallel_api(api, [lambda client, name=name: client._request('GET', '/settings/' + name) for name in names])
    for name, rows in zip(names, results):
        if not isinstance(rows, list):
            raise ValueError('Invalid service settings from Seerr')
        for row in rows:
            config = arr_config(name, row)
            identity = f"{name}:{int(row['id'])}"
            if identity in configs:
                raise ValueError('Duplicate Seerr service instance')
            configs[identity] = config
    return configs
