"""Only verified viewers receive operational request timing."""
from aiohttp import web

VERIFIED_TIMING = web.RequestKey('verified-timing', bool)
