import logging
from typing import Iterator

import grpc

def init_grpc_channel(url: str) -> Iterator[grpc.Channel]:
    logging.info(f"Connecting to Brain at {url}...")

    options = [
        ("grpc.keepalive_time_ms", 10000),
        ("grpc.keepalive_timeout_ms", 5000),
        ("grpc.keepalive_permit_without_calls", 1),
    ]

    channel = grpc.insecure_channel(url, options=options)

    yield channel

    logging.info("CLosing Brain connection...")
    channel.close()
    