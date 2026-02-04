import grpc

from config import config
from generated import brain_pb2_grpc, brain_pb2

def ping_brain() -> brain_pb2.PingResponse:
    with grpc.insecure_channel(config.brain_url) as channel:
        stub = brain_pb2_grpc.BrainStub(channel)
        try:
            response = stub.Ping(brain_pb2.PingRequest(message="Hello, Brain!"))
            return response
        except grpc.RpcError as e:
            print(f"gRPC error: {e.code()} - {e.details()}")
            raise

if __name__ == "__main__":
    ping_brain()