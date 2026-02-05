from infra.brain import BrainClient

class UtilService:
    def __init__(self, brain: BrainClient) -> None:
        self._brain = brain

    def ping(self, message: str) -> str:
        response = self._brain.ping(message)
        return f"Brain says: {response}"
