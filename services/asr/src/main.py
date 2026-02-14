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

    listen_addr = f"[::]:{settings.grpc_port}"
    server.add_insecure_port(listen_addr)

    await server.start()
    logging.info("ASR gRPC server listening on %s", listen_addr)

    try:
        await server.wait_for_termination()
    except asyncio.CancelledError:
        pass
    finally:
        await server.stop(grace=5)
        logging.info("ASR gRPC server stopped")

def main() -> None:
    try:
        asyncio.run(serve())
    except KeyboardInterrupt:
        pass

if __name__ == "__main__":
    main()
