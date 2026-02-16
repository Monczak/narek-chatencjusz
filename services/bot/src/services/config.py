import logging
from generated import brain_pb2, brain_pb2_grpc


class ConfigService:
    def __init__(self, brain: brain_pb2_grpc.BrainStub) -> None:
        self.brain = brain

    async def get_settings(self, guild_id: int) -> brain_pb2.GetGuildSettingsResponse:
        req = brain_pb2.GetGuildSettingsRequest(guild_id=guild_id)
        return await self.brain.GetGuildSettings(req)  # type: ignore

    async def set_system_prompt(self, guild_id: int, text: str) -> brain_pb2.UpdateGuildSettingsResponse:
        return await self._patch(guild_id, system_prompt=text)

    async def clear_system_prompt(self, guild_id: int) -> brain_pb2.UpdateGuildSettingsResponse:
        return await self._clear(guild_id, "system_prompt")

    async def set_custom_instructions(self, guild_id: int, text: str) -> brain_pb2.UpdateGuildSettingsResponse:
        return await self._patch(guild_id, custom_instructions=text)

    async def clear_custom_instructions(self, guild_id: int) -> brain_pb2.UpdateGuildSettingsResponse:
        return await self._clear(guild_id, "custom_instructions")

    async def set_bot_name(self, guild_id: int, name: str) -> brain_pb2.UpdateGuildSettingsResponse:
        return await self._patch(guild_id, bot_name=name)

    async def set_temperature(self, guild_id: int, value: float) -> brain_pb2.UpdateGuildSettingsResponse:
        return await self._patch(guild_id, temperature=value)

    async def set_silence_threshold(self, guild_id: int, ms: int) -> brain_pb2.UpdateGuildSettingsResponse:
        return await self._patch(guild_id, silence_threshold_ms=ms)

    async def set_ramble_enabled(self, guild_id: int, enabled: bool) -> brain_pb2.UpdateGuildSettingsResponse:
        return await self._patch(guild_id, ramble_mode_enabled=enabled)

    async def set_ramble_threshold(self, guild_id: int, seconds: int) -> brain_pb2.UpdateGuildSettingsResponse:
        return await self._patch(guild_id, ramble_threshold_ms=seconds * 1000)

    async def _patch(self, guild_id: int, **kwargs) -> brain_pb2.UpdateGuildSettingsResponse:
        patch = brain_pb2.GuildLlmConfig(**kwargs)
        req = brain_pb2.UpdateGuildSettingsRequest(guild_id=guild_id, patch=patch)
        try:
            return await self.brain.UpdateGuildSettings(req)  # type: ignore
        except Exception as e:
            logging.error("UpdateGuildSettings RPC failed: %s", e)
            raise

    async def _clear(self, guild_id: int, *field_names: str) -> brain_pb2.UpdateGuildSettingsResponse:
        req = brain_pb2.UpdateGuildSettingsRequest(
            guild_id=guild_id,
            patch=brain_pb2.GuildLlmConfig(),
            clear_fields=list(field_names),
        )
        try:
            return await self.brain.UpdateGuildSettings(req)  # type: ignore
        except Exception as e:
            logging.error("UpdateGuildSettings (clear) RPC failed: %s", e)
            raise
