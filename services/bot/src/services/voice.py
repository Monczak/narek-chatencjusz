import asyncio
import discord
import logging
from typing import Optional, Tuple
from generated import brain_pb2, brain_pb2_grpc
from services.state import StateService, VoiceTransitionType
from services.event_stream import EventStreamService
from services.interaction import InteractionService
from services.response import ResponseService

class VoiceService:
    def __init__(
        self, 
        brain_stub: brain_pb2_grpc.BrainStub,
        response_service: ResponseService,
        interaction_service: InteractionService,
        event_stream: EventStreamService,
        state_service: StateService
    ) -> None:
        self.brain = brain_stub
        self.response = response_service
        self.interaction = interaction_service
        self.event_stream = event_stream
        self.state = state_service

        self.bot: discord.Bot | None = None # Injected later

    def set_bot(self, bot: discord.Bot):
        self.bot = bot

    async def request_join(self, guild_id: str, channel_id: str, node_id: str, correlation_id: str) -> Tuple[bool, str]:
        try:
            req = brain_pb2.JoinChannelRequest(
                guild_id=guild_id,
                channel_id=channel_id,
                node_id=node_id,
                correlation_id=correlation_id
            )
            res = await self.brain.JoinChannel(req) # type: ignore (BrainAsyncStub)
            return res.success, res.message
        except Exception as e:
            logging.error(f"Brain voice join error: {e}")
            raise
    
    async def request_leave(self, guild_id: str, node_id: str, correlation_id: str) -> bool:
        try:
            req = brain_pb2.LeaveChannelRequest(
                guild_id=guild_id, 
                node_id=node_id,
                correlation_id=correlation_id
            )
            res = await self.brain.LeaveChannel(req) # type: ignore (BrainAsyncStub)
            return res.success
        except Exception as e:
            logging.error(f"Brain voice leave error: {e}")
            raise

    async def execute_connect(self, guild_id: str, channel_id: str, correlation_id: str):
        if not self.bot:
            raise RuntimeError("Bot not set")
        
        self.state.register_intent(guild_id, VoiceTransitionType.CONNECT, channel_id)

        try:
            guild = self.bot.get_guild(int(guild_id))
            if not guild:
                logging.warning(f"Guild {guild_id} not found during connect event handling")
                return
            
            channel_to_join = guild.get_channel(int(channel_id))
            if not channel_to_join or not isinstance(channel_to_join, discord.VoiceChannel):
                logging.warning(f"Channel {channel_id} is invalid")
                return
            
            is_moving = guild.voice_client is not None and guild.voice_client.is_connected()

            try:
                if is_moving:
                    await guild.voice_client.move_to(channel_to_join) # type: ignore (Pylance doesn't understand logic)
                else:
                    await channel_to_join.connect()
            except Exception as e:
                logging.warning(f"Standard join failed ({e}) -- attempting hard reconnect")
                try:
                    if guild.voice_client:
                        # Try to disconnect first
                        try:
                            await guild.voice_client.disconnect(force=True)
                        except Exception:
                            pass

                        for _ in range(5):
                            if guild.voice_client is None:
                                break
                            await asyncio.sleep(0.5)
                        
                        # Last ditch: if it's still there, we can't connect
                        if guild.voice_client is not None:
                            logging.error("Voice client is stuck (zombie state). Cannot reconnect.")
                            raise RuntimeError("Voice client stuck in zombie state")
                        
                    await channel_to_join.connect()
                except Exception as e2:
                    logging.error(f"Hard reconnect failed: {e2}")
                    self.state.consume_intent(guild_id)
                    await self.response.complete(correlation_id, success=False, title="Connection failed", description=str(e2))
                    return

            if not is_moving:
                self.event_stream.push_session_state_update(guild_id, brain_pb2.SessionUpdate.ChangeType.STARTED)
            else:
                self.event_stream.push_session_state_update(guild_id, brain_pb2.SessionUpdate.ChangeType.MOVED)

            await self.response.complete(correlation_id, success=True, title="Connected", description=f"Joined {channel_to_join.mention}")
        
        except Exception as e:
            self.state.consume_intent(guild_id)
            logging.error(f"Error handling execute_connect: {e}")
            await self.response.complete(correlation_id, success=False, title="Connection failed", description=str(e))
    
    async def execute_disconnect(self, guild_id: str, correlation_id: str | None):
        if not self.bot:
            raise RuntimeError("Bot not set")
        
        self.state.register_intent(guild_id, VoiceTransitionType.DISCONNECT)
        
        try:
            guild = self.bot.get_guild(int(guild_id))
            if guild and guild.voice_client:
                await guild.voice_client.disconnect()

            self.event_stream.push_session_state_update(guild_id, brain_pb2.SessionUpdate.ChangeType.ENDED)
            await self.response.complete(correlation_id, success=True, title="Disconnected", description=f"Left the voice channel")
        except Exception:
            self.state.consume_intent(guild_id)
            raise

