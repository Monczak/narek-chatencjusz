from generated import brain_pb2, brain_pb2_grpc

class VoiceService:
    def __init__(self, brain: brain_pb2_grpc.BrainStub) -> None:
        self.brain = brain
