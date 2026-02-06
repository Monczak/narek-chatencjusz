from typing import AsyncIterator, Iterator

import grpc
import grpc.aio
from valkey import Valkey

async def init_async_grpc_channel(url: str) -> AsyncIterator[grpc.aio.Channel]:
    channel = grpc.aio.insecure_channel(url)
    yield channel
    await channel.close()
    
def init_valkey_client(url: str, decode_responses: bool = True) -> Iterator[Valkey]:
    client = Valkey.from_url(url, decode_responses=decode_responses)
    yield client
    client.close()
