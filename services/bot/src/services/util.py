from generated import brain_pb2, brain_pb2_grpc

class UtilService:
    def __init__(self, brain: brain_pb2_grpc.BrainStub) -> None:
        self.brain = brain

    async def ping(self, message: str) -> str:
        req = brain_pb2.PingRequest(message=message)
        res = await self.brain.Ping(req) # type: ignore (BrainAsyncStub)
        return f"Brain says: {res.message}"
