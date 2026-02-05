import grpc
import logging

from generated import brain_pb2, brain_pb2_grpc

class BrainClient:
    def __init__(self, channel: grpc.Channel):
        self._stub = brain_pb2_grpc.BrainStub(channel)
    
    def ping(self, message: str) -> str:
        request = brain_pb2.PingRequest(message=message)
        try:
            response = self._stub.Ping(request)
            return response.message
        except grpc.RpcError as e:
            logging.error(f"Brain gRPC call failed: {e}")
            raise ConnectionError("Could not reach Brain") from e
