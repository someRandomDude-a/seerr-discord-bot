import requests
import re
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
        return {'items': self.request('GET', resource), 'disks': self.request('GET', 'diskspace')}

    def search(self, query):
        resource = 'album' if self.config.name == 'lidarr' else 'book'
        return self.request('GET', resource + '/lookup', params={'term': query})

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
