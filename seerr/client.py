import requests
from typing import Optional, Dict, Any, List
from .exceptions import SeerrAPIError

class SeerrAPI:
    def __init__(self, base_url: str, api_key: Optional[str] = None, timeout: int = 30):
        """
        Initialize the Seerr API client.
        
        Args:
            base_url: Base URL of the Seerr instance (e.g., 'http://localhost:5055')
            api_key: API key for authentication (can be None if using cookie-based auth)
            timeout: Request timeout in seconds
        """
        self.base_url = base_url.rstrip('/') + '/api/v1'
        self.api_key = api_key
        self.timeout = timeout
        self.session = requests.Session()
        if api_key:
            self.session.headers.update({'X-Api-Key': api_key})

    def _request(
        self,
        method: str,
        endpoint: str,
        json: Optional[Dict[str, Any]] = None,
        params: Optional[Dict[str, Any]] = None
    ) -> Any:
        """Internal method to make API requests and handle errors."""
        url = f"{self.base_url}{endpoint}"
        try:
            response = self.session.request(
                method=method,
                url=url,
                json=json,
                params=params,
                timeout=self.timeout
            )
            response.raise_for_status()
            return response.json() if response.text else None
        except requests.exceptions.HTTPError as e:
            raise SeerrAPIError(f"HTTP Error {e.response.status_code}: {e.response.text}") from e
        except requests.exceptions.RequestException as e:
            raise SeerrAPIError(f"Request failed: {e}") from e

    # Authentication
    def get_current_user(self) -> Dict[str, Any]:
        return self._request('GET', '/auth/me')

    def logout_user(self) -> None:
        self._request('POST', '/auth/logout')

    def login_with_plex(self, auth_token: str) -> Dict[str, Any]:
        return self._request('POST', '/auth/plex', json={"authToken": auth_token})

    def login_with_jellyfin(
        self, username: str, password: str, hostname: Optional[str] = None, server_type: int = 0
    ) -> Dict[str, Any]:
        """Authenticate with Jellyfin/Emby credentials. server_type: 0=Jellyfin, 1=Emby."""
        body = {
            "username": username,
            "password": password,
            "serverType": server_type
        }
        if hostname is not None:
            body["hostname"] = hostname
        return self._request('POST', '/auth/jellyfin', json=body)

    def login_locally(self, email: str, password: str) -> Dict[str, Any]:
        return self._request('POST', '/auth/local', json={
            "email": email,
            "password": password
        })

    def reset_password(self, email: str) -> Dict[str, Any]:
        return self._request('POST', '/auth/reset-password', json={"email": email})

    # Request Management
    def list_requests(
        self,
        take: int = 25,
        skip: int = 0,
        filter_status: Optional[str] = None,
        media_type: Optional[str] = None,
        sort: Optional[str] = None,
        requested_by: Optional[int] = None
    ) -> Dict[str, Any]:
        params = {'take': take, 'skip': skip}
        if filter_status:
            params['filter'] = filter_status
        if media_type:
            params['mediaType'] = media_type
        if sort:
            params['sort'] = sort
        if requested_by:
            params['requestedBy'] = requested_by
        return self._request('GET', '/request', params=params)

    def create_request(self, media_type: str, media_id: int) -> Dict[str, Any]:
        return self._request('POST', '/request', json={
            "mediaType": media_type,
            "mediaId": media_id
        })

    def get_request(self, request_id: int) -> Dict[str, Any]:
        return self._request('GET', f'/request/{request_id}')

    def update_request(self, request_id: int, updates: Dict[str, Any]) -> Dict[str, Any]:
        return self._request('PUT', f'/request/{request_id}', json=updates)

    def delete_request(self, request_id: int) -> None:
        self._request('DELETE', f'/request/{request_id}')

    def approve_request(self, request_id: int) -> Dict[str, Any]:
        return self._request('POST', f'/request/{request_id}/approve')

    def decline_request(self, request_id: int) -> Dict[str, Any]:
        return self._request('POST', f'/request/{request_id}/decline')

    # User Management
    def list_users(
        self,
        take: int = 25,
        skip: int = 0,
        sort: Optional[str] = None,
        q: Optional[str] = None,
        include_ids: Optional[List[int]] = None
    ) -> Dict[str, Any]:
        params = {'take': take, 'skip': skip}
        if sort:
            params['sort'] = sort
        if q:
            params['q'] = q
        if include_ids:
            params['includeIds'] = ','.join(str(uid) for uid in include_ids)
        return self._request('GET', '/user', params=params)

    def create_user(self, user_data: Dict[str, Any]) -> Dict[str, Any]:
        return self._request('POST', '/user', json=user_data)

    def get_user(self, user_id: int) -> Dict[str, Any]:
        return self._request('GET', f'/user/{user_id}')

    def update_user(self, user_id: int, updates: Dict[str, Any]) -> Dict[str, Any]:
        return self._request('PUT', f'/user/{user_id}', json=updates)

    def delete_user(self, user_id: int) -> None:
        self._request('DELETE', f'/user/{user_id}')

    def get_user_quota(self, user_id: int) -> Dict[str, Any]:
        return self._request('GET', f'/user/{user_id}/quota')

    def get_user_requests(self, user_id: int) -> Dict[str, Any]:
        return self._request('GET', f'/user/{user_id}/requests')

    def get_user_watchlist(self, user_id: int) -> Dict[str, Any]:
        return self._request('GET', f'/user/{user_id}/watchlist')

    def add_to_watchlist(self, user_id: int, media: Dict[str, Any]) -> Dict[str, Any]:
        return self._request('POST', f'/user/{user_id}/watchlist', json=media)

    def register_push_subscription(self, subscription: Dict[str, Any]) -> None:
        self._request('POST', '/user/registerPushSubscription', json=subscription)

    # Settings & System
    def get_main_settings(self) -> Dict[str, Any]:
        return self._request('GET', '/settings/main')

    def update_main_settings(self, settings: Dict[str, Any]) -> Dict[str, Any]:
        return self._request('POST', '/settings/main', json=settings)

    def regenerate_api_key(self) -> Dict[str, Any]:
        return self._request('POST', '/settings/main/regenerate')

    def get_public_settings(self) -> Dict[str, Any]:
        old_headers = self.session.headers.copy()
        self.session.headers.pop('X-Api-Key', None)
        try:
            return self._request('GET', '/settings/public')
        finally:
            self.session.headers = old_headers

    def get_network_settings(self) -> Dict[str, Any]:
        return self._request('GET', '/settings/network')

    def update_network_settings(self, settings: Dict[str, Any]) -> Dict[str, Any]:
        return self._request('POST', '/settings/network', json=settings)

    def list_jobs(self) -> List[Dict[str, Any]]:
        return self._request('GET', '/settings/jobs')

    def run_job(self, job_id: str) -> Dict[str, Any]:
        return self._request('POST', f'/settings/jobs/{job_id}/run')

    def set_job_schedule(self, job_id: str, schedule: str) -> Dict[str, Any]:
        return self._request('POST', f'/settings/jobs/{job_id}/schedule', json={"schedule": schedule})

    def get_cache_stats(self) -> Dict[str, Any]:
        return self._request('GET', '/settings/cache')

    def flush_cache(self, cache_id: str) -> Dict[str, Any]:
        return self._request('POST', f'/settings/cache/{cache_id}/flush')

    def get_logs(
        self, take: int = 100, skip: int = 0, level: Optional[str] = None
    ) -> Dict[str, Any]:
        params = {'take': take, 'skip': skip}
        if level:
            params['level'] = level
        return self._request('GET', '/settings/logs', params=params)

    # Media Discovery & Details
    def search(self, query: str, page: int = 1, language: str = "en") -> Dict[str, Any]:
        params = {'query': query, 'page': page, 'language': language}
        return self._request('GET', '/search', params=params)

    def get_movie_details(self, movie_id: int) -> Dict[str, Any]:
        return self._request('GET', f'/movie/{movie_id}')

    def get_tv_details(self, tv_id: int) -> Dict[str, Any]:
        return self._request('GET', f'/tv/{tv_id}')

    def discover_movies(self, **filters) -> Dict[str, Any]:
        return self._request('GET', '/discover/movies', params=filters)

    def discover_tv(self, **filters) -> Dict[str, Any]:
        return self._request('GET', '/discover/tv', params=filters)

    def get_trending(self, media_type: str = "all") -> Dict[str, Any]:
        params = {'mediaType': media_type}
        return self._request('GET', '/discover/trending', params=params)

    def get_popular(self, media_type: str = "all") -> Dict[str, Any]:
        params = {'mediaType': media_type}
        return self._request('GET', '/discover/popular', params=params)

    def get_genres(self, media_type: str = "movie") -> Dict[str, Any]:
        params = {'mediaType': media_type}
        return self._request('GET', '/discover/genreslider', params=params)