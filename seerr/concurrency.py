"""Bounded independent reads. Drain workers before releasing snapshot/write locks."""
from concurrent.futures import ThreadPoolExecutor
from contextlib import ExitStack, contextmanager
import threading

from .client import SeerrAPI


def parallel(function, values, workers=4):
    values = list(values)
    if not values:
        return []
    if len(values) == 1:
        return [function(values[0])]
    with ThreadPoolExecutor(max_workers=min(workers, len(values)), thread_name_prefix='media-read') as pool:
        futures = [pool.submit(function, value) for value in values]
        try:
            return [future.result() for future in futures]
        except BaseException:
            for future in futures:
                future.cancel()
            raise


@contextmanager
def independent(api):
    # requests.Session has mutable cookies/CSRF state: never share it across workers.
    # Small protocol fixtures can provide their own stateless read implementation.
    if not isinstance(api, SeerrAPI):
        yield api
        return
    client = api.fork()
    try:
        yield client
    finally:
        client.close()


def parallel_api(api, functions):
    local = threading.local()
    lock = threading.Lock()
    stack = ExitStack()
    def read(function):
        if not hasattr(local, 'client'):
            # One connection pool per worker, never a shared mutable Session.
            with lock:
                local.client = stack.enter_context(independent(api))
        return function(local.client)
    try:
        return parallel(read, functions)
    finally:
        stack.close()  # parallel() has drained every worker before closing pools.
