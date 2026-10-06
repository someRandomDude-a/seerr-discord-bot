import time
from collections import deque


class UserError(Exception):
    """A safe, user-facing error; never includes upstream bodies or secrets."""


class RateLimiter:
    def __init__(self, count, window, clock=time.monotonic):
        self.count, self.window, self.clock = count, window, clock
        self.buckets = {}

    def check(self, key):
        now = self.clock()
        # Expire inactive keys to bound memory, including unauthenticated HTTP clients.
        self.buckets = {k: q for k, q in self.buckets.items() if q and q[-1] > now - self.window}
        queue = self.buckets.setdefault(key, deque())
        while queue and queue[0] <= now - self.window:
            queue.popleft()
        if len(queue) >= self.count:
            raise UserError(f'Too many attempts. Try again in {max(1, int(self.window - (now - queue[0])) + 1)} seconds.')
        queue.append(now)

