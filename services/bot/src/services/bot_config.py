import logging
from generated import brain_pb2, brain_pb2_grpc

class BotConfigService:
    def __init__(self, brain: brain_pb2_grpc.BrainStub) -> None:
        self.brain = brain
        self._skip_silence_check: bool = False

    @property
    def skip_silence_check(self) -> bool:
        return self._skip_silence_check

    async def refresh(self) -> None:
        try:
            req = brain_pb2.GetBotSettingsRequest()
            resp: brain_pb2.GetBotSettingsResponse = await self.brain.GetBotSettings(req)  # type: ignore
            self._skip_silence_check = resp.config.skip_silence_check
            logging.info("BotConfigService refreshed: skip_silence_check=%s", self._skip_silence_check)
        except Exception as e:
            logging.warning("BotConfigService: failed to fetch bot settings from Brain: %s", e)

    async def set_skip_silence_check(self, value: bool) -> brain_pb2.UpdateBotSettingsResponse:
        patch = brain_pb2.BotNodeConfig(skip_silence_check=value)
        req = brain_pb2.UpdateBotSettingsRequest(patch=patch)
        return await self.brain.UpdateBotSettings(req)  # type: ignore
