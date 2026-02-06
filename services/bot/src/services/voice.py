import asyncio
import discord
import logging
from typing import Optional, Tuple
from generated import brain_pb2, brain_pb2_grpc
from services.interaction import InteractionService
from services.response import ResponseService

class VoiceService:
    def __init__(
        self, 
        brain_stub: brain_pb2_grpc.BrainStub,
        response_service: ResponseService,
        interaction_service: InteractionService
    ) -> None:
        self.brain = brain_stub
        self.response = response_service
        self.interaction = interaction_service
        self.bot: discord.Bot | None = None # Injected later

    def set_bot(self, bot: discord.Bot):
        self.bot = bot

    def request_join(self, guild_id: str, channel_id: str, node_id: str, correlation_id: str) -> Tuple[bool, str]:
        try:
            req = brain_pb2.JoinChannelRequest(
                guild_id=guild_id,
                channel_id=channel_id,
                node_id=node_id,
                correlation_id=correlation_id
            )
            res = self.brain.JoinChannel(req)
            return res.success, res.message
        except Exception as e:
            logging.error(f"Brain voice join error: {e}")
            raise
    
    def request_leave(self, guild_id: str, node_id: str, correlation_id: str) -> None:
        try:
            req = brain_pb2.LeaveChannelRequest(
                guild_id=guild_id, 
                node_id=node_id,
                correlation_id=correlation_id
            )
            self.brain.LeaveChannel(req)
        except Exception as e:
            logging.error(f"Brain voice leave error: {e}")
    
    async def execute_connect(self, guild_id: str, channel_id: str, correlation_id: str):
        if not self.bot:
            raise RuntimeError("Bot not set")

        try:
            guild = self.bot.get_guild(int(guild_id))
            if not guild:
                logging.warning(f"Guild {guild_id} not found during connect event handling")
                return
            
            channel_to_join = guild.get_channel(int(channel_id))
            if not channel_to_join or not isinstance(channel_to_join, discord.VoiceChannel):
                logging.warning(f"Channel {channel_id} is invalid")
                return
            
            try:
                if guild.voice_client:
                    await guild.voice_client.move_to(channel_to_join)
                else:
                    await channel_to_join.connect()
            except Exception as e:
                logging.warning(f"Standard join failed ({e}) -- attempting hard reconnect")
                try:
                    if guild.voice_client:
                        await guild.voice_client.disconnect(force=True)
                        await asyncio.sleep(0.5)
                    await channel_to_join.connect()
                except Exception as e2:
                    logging.error(f"Hard reconnect failed: {e2}")

            await self._complete_interaction(correlation_id, success=True, title="Connected", msg=f"Joined {channel_to_join.mention}")
        
        except Exception as e:
            logging.error(f"Error handling execute_connect: {e}")
            await self._complete_interaction(correlation_id, success=False, title="Connection failed", msg=str(e))
    
    async def execute_disconnect(self, guild_id: str, correlation_id: str | None):
        if not self.bot:
            raise RuntimeError("Bot not set")
        
        guild = self.bot.get_guild(int(guild_id))
        if guild and guild.voice_client:
            await guild.voice_client.disconnect()

        await self._complete_interaction(correlation_id, success=True, title="Disconnected", msg=f"Left the voice channel")

    async def _complete_interaction(self, correlation_id: str | None, success: bool, title: str, msg: str):
        ctx = self.interaction.pop(correlation_id)
        if not ctx:
            return

        if success:
            await self.response.respond_success(ctx, title, msg)
        else:
            await self.response.respond_error(ctx, msg)
