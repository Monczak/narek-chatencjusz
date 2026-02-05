from valkey import Valkey

class ValkeyClient:
    def __init__(self, url: str):
        self._client = Valkey.from_url(url)

    def close(self):
        self._client.close()
