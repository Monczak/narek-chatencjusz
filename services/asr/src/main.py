import asyncio
import logging
import grpc
from config import Settings
from generated import asr_pb2_grpc
from servicer import AsrServicer

logging.basicConfig(level=logging.INFO)

async def serve() -> None:
    settings = Settings()

    server = grpc.aio.server()
    asr_pb2_grpc.add_AsrServicer_to_server(AsrServicer(settings), server)

def main() -> None:
    try:
        asyncio.run(serve())
    except KeyboardInterrupt:
        pass

if __name__ == "__main__":
    main()
