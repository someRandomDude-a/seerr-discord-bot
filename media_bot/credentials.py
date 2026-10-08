"""Encrypt retained Jellyfin user tokens; keep the key outside SQLite and the browser."""
import os
from pathlib import Path

from cryptography.fernet import Fernet


class CredentialVault:
    def __init__(self, database):
        self.path = Path(database).parent / 'jellyfin.key'

    def cipher(self, create=False):
        if create and not self.path.exists():
            self.path.parent.mkdir(parents=True, exist_ok=True)
            try:
                fd = os.open(self.path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
            except FileExistsError:
                pass
            else:
                with os.fdopen(fd, 'wb') as stream:
                    stream.write(Fernet.generate_key())
                    stream.flush()
                    os.fsync(stream.fileno())
        return Fernet(self.path.read_bytes())

    def encrypt(self, data):
        return self.cipher(create=True).encrypt(data.encode()).decode()

    def decrypt(self, data):
        return self.cipher().decrypt(data.encode()).decode()
