import logging
from typing import Iterator

import grpc
from valkey import Valkey

def init_grpc_channel(url: str) -> Iterator[grpc.Channel]:
    logging.info(f"Connecting to gRPC endpoint at {url}...")

    options = [
        ("grpc.keepalive_time_ms", 10000),
        ("grpc.keepalive_timeout_ms", 5000),
        ("grpc.keepalive_permit_without_calls", 1),
    ]

    channel = grpc.insecure_channel(url, options=options)

    yield channel

    logging.info(f"Closing gRPC endpoint connection ({url})...")
    channel.close()
    
def init_valkey_client(url: str, decode_responses: bool = True) -> Iterator[Valkey]:
    client = Valkey.from_url(url, decode_responses=decode_responses)
    
    yield client

    client.close()
