import requests
import re
from seerr.concurrency import parallel
from .security import UserError


class ArrClient:
    def __init__(self, config, timeout=15):
        self.config, self.timeout = config, timeout
        self.version = 'v3' if config.name in ('radarr', 'sonarr') else 'v1'

    def request(self, method, path, **kwargs):
        try:
            response = requests.request(method, f'{self.config.url}/api/{self.version}/{path}',
                headers={'X-Api-Key': self.config.key}, timeout=self.timeout, allow_redirects=False, **kwargs)
            if not 200 <= response.status_code < 300:
                raise UserError(f'{self.config.name.title()} is unavailable or rejected the operation.')
            return response.json() if response.content else None
        except (requests.RequestException, ValueError):
            raise UserError(f'{self.config.name.title()} is unreachable or returned invalid data.') from None

    def snapshot(self):
        resource = {'radarr': 'movie', 'sonarr': 'series', 'lidarr': 'album', 'readarr': 'book'}[self.config.name]
        items, disks = parallel(lambda path: self.request('GET', path), [resource, 'diskspace'], workers=2)
        return {'items': items, 'disks': disks}

    def search(self, query):
        resource = 'album' if self.config.name == 'lidarr' else 'book'
        return self.request('GET', resource + '/lookup', params={'term': query})

    def monitoring_target(self, provider_id):
        """Live library records only; lookup results can describe absent media."""
        resource, provider = ('movie', 'tmdbId') if self.config.name == 'radarr' else ('series', 'tvdbId')
        if self.config.name not in ('radarr', 'sonarr'):
            raise UserError('Unmonitoring is only supported for movies and series.')
        rows = self.request('GET', resource, params={provider: provider_id})
        if not isinstance(rows, list) or any(not isinstance(row, dict) or type(row.get(provider)) is not int or
                row[provider] <= 0 or type(row.get('id')) is not int or row['id'] <= 0 for row in rows):
            raise UserError('The service returned an invalid monitoring target.')
        matches = [row for row in rows if row[provider] == provider_id]
        if len(matches) > 1:
            raise UserError('The monitoring target is ambiguous.')
        return matches[0] if matches else None

    def _series_episodes(self, library_id):
        episodes = self.request('GET', 'episode', params={'seriesId': library_id})
        if not isinstance(episodes, list) or any(not isinstance(ep, dict) or type(ep.get('seriesId')) is not int or
                ep['seriesId'] != library_id or type(ep.get('id')) is not int or ep['id'] <= 0 for ep in episodes):
            raise UserError('The service returned invalid episodes; monitoring cleanup is pending.')
        return episodes

    def unmonitor_deleted(self, provider_id, library_id):
        item = self.monitoring_target(provider_id)
        if item is None:
            return  # Seerr normally removes the Arr entry; never recreate it.
        if library_id is None or item['id'] != library_id:
            raise UserError('Files were deleted, but the library entry changed. Verify monitoring in the service.')
        if arr_item(self.config.name, item)['available']:
            raise UserError('Files are still reported by the service. Monitoring cleanup is pending; file deletion will not be repeated.')
        if self.config.name == 'radarr':
            if item.get('monitored') is not False:
                self.request('PUT', 'movie/editor', json={'movieIds': [library_id], 'monitored': False})
        else:
            # Clear season/episode flags as well, including future seasons.
            body = {**item, 'monitored': False, 'monitorNewItems': 'none',
                'seasons': [{**season, 'monitored': False} for season in item.get('seasons', [])]}
            self.request('PUT', f'series/{library_id}', json=body)
            episodes = self._series_episodes(library_id)
            ids = [ep['id'] for ep in episodes if ep.get('monitored') is not False]
            for start in range(0, len(ids), 500):
                self.request('PUT', 'episode/monitor', json={'episodeIds': ids[start:start + 500], 'monitored': False})
            if ids and any(ep.get('monitored') is not False for ep in self._series_episodes(library_id)):
                raise UserError('Episode unmonitoring was not confirmed; monitoring cleanup is pending.')
        fresh = self.monitoring_target(provider_id)
        if fresh is not None and (fresh['id'] != library_id or fresh.get('monitored') is not False or
                self.config.name == 'sonarr' and (fresh.get('monitorNewItems') != 'none' or any(season.get('monitored') is not False for season in fresh.get('seasons', [])))):
            raise UserError('Unmonitoring was not confirmed; monitoring cleanup is pending.')

    def validate_config(self):
        c = self.config
        if not c.root or not c.quality or not c.metadata:
            raise UserError(f'Configure {c.name.upper()} root, quality and metadata profiles before submitting requests.')

    def add(self, item):
        self.validate_config()
        c = self.config
        parent_key = 'artist' if c.name == 'lidarr' else 'author'
        parent = dict(item.get(parent_key) or {})
        if not parent:
            raise UserError('The lookup did not return artist/author metadata. Add the parent in the service first.')
        parent.update(rootFolderPath=c.root, qualityProfileId=c.quality, metadataProfileId=c.metadata,
                      monitored=True, monitorNewItems='none')
        body = dict(item)
        body.update(monitored=True)
        body[parent_key] = parent
        if c.name == 'lidarr':
            body['addOptions'] = {'searchForNewAlbum': True}
        else:
            body['addOptions'] = {'searchForNewBook': True}
        return self.request('POST', 'album' if c.name == 'lidarr' else 'book', json=body)


def external_id(name, item):
    name = name.split(':')[0]
    return str(item.get({'radarr': 'tmdbId', 'sonarr': 'tvdbId', 'lidarr': 'foreignAlbumId', 'readarr': 'foreignBookId'}[name]) or '')


def arr_item(name, item):
    source = name
    name = name.split(':')[0]
    kind = {'radarr': 'movie', 'sonarr': 'tv', 'lidarr': 'music', 'readarr': 'book'}[name]
    stats = item.get('statistics') or {}
    size = stats.get('sizeOnDisk', item.get('sizeOnDisk', 0)) or 0
    available = bool(item.get('hasFile') or item.get('bookFile') or size > 0 or
                     stats.get('trackFileCount', 0) or stats.get('bookFileCount', 0) or stats.get('episodeFileCount', 0))
    parent = item.get('artist') or item.get('author') or {}
    subtitle = parent.get('artistName') or parent.get('authorName') or ''
    if name == 'lidarr' and stats.get('totalTrackCount'):
        subtitle += f" · {stats.get('trackFileCount', 0)}/{stats['totalTrackCount']} tracks"
    if name == 'sonarr' and stats.get('episodeCount'):
        subtitle += f" · {stats.get('episodeFileCount', 0)}/{stats['episodeCount']} episodes"
    poster = None
    for image in item.get('images') or []:
        if image.get('coverType') in ('poster', 'cover'):
            match = re.fullmatch(r'https://image\.tmdb\.org/t/p/(?:original|w[0-9]+)(/[A-Za-z0-9_-]{1,100}\.(?:jpg|png|webp))', image.get('remoteUrl') or '')
            if match:
                poster = match.group(1)
                break
    return {'kind': kind, 'external_id': external_id(name, item), 'title': item.get('title') or 'Untitled',
            'overview': item.get('overview') or '', 'available': available, 'size': size,
            'requestable': not bool(item.get('id')),
            'subtitle': subtitle.strip(' ·'), 'source': source,
            'raw': item, 'jellyfin_id': None, 'poster_path': poster}
