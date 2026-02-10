import asyncio
import discord
import logging
from typing import Tuple
from generated import brain_pb2, brain_pb2_grpc
from services.audio_stream import AudioStreamService
from services.vad import VADService
from services.network_sink import GrpcVadAudioSink
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
        state_service: StateService,
        audio_stream: AudioStreamService,
        vad_service: VADService
    ) -> None:
        self.brain = brain_stub
        self.response = response_service
        self.interaction = interaction_service
        self.event_stream = event_stream
        self.state = state_service
        self.audio_stream = audio_stream
        self.vad = vad_service

        self.bot: discord.Bot | None = None # Injected later

        try:
            self.vad.load_model()
        except Exception as e:
            logging.error(f"Failed to preload Silero VAD: {e}")
            raise

    def set_bot(self, bot: discord.Bot):
        self.bot = bot

    async def request_join(self, guild: discord.Guild, channel: discord.VoiceChannel, node_id: str, correlation_id: str) -> Tuple[bool, str]:
        try:
            req = brain_pb2.JoinChannelRequest(
                guild=brain_pb2.GuildContext(id=guild.id, name=guild.name),
                channel=brain_pb2.ChannelContext(id=channel.id, name=channel.name),
                node_id=node_id,
                correlation_id=correlation_id
            )
            res = await self.brain.JoinChannel(req) # type: ignore (BrainAsyncStub)
            
            if res.success and res.session_id:
                self.state.set_session_id(guild.id, res.session_id)
                logging.info(f"Received session ID {res.session_id} from Brain for guild {guild.id}")
            
            return res.success, res.message
        except Exception as e:
            logging.error(f"Brain voice join error: {e}")
            raise
    
    async def request_leave(self, guild: discord.Guild, node_id: str, correlation_id: str) -> bool:
        try:
            req = brain_pb2.LeaveChannelRequest(
                guild=brain_pb2.GuildContext(id=guild.id, name=guild.name), 
                node_id=node_id,
                correlation_id=correlation_id
            )
            res = await self.brain.LeaveChannel(req) # type: ignore (BrainAsyncStub)
            return res.success
        except Exception as e:
            logging.error(f"Brain voice leave error: {e}")
            raise

    async def execute_connect(self, guild_ctx: brain_pb2.GuildContext, channel_ctx: brain_pb2.ChannelContext, correlation_id: str, session_id: str | None = None):
        if not self.bot:
            raise RuntimeError("Bot not set")
        
        if session_id:
            self.state.set_session_id(guild_ctx.id, session_id)
        
        self.state.register_intent(
            guild_ctx.id, 
            VoiceTransitionType.CONNECT, 
            channel_ctx.id,
            session_id=session_id
        )

        try:
            guild = self.bot.get_guild(int(guild_ctx.id))
            if not guild:
                logging.warning(f"Guild {guild_ctx.id} not found during connect event handling")
                return
            
            channel_to_join = guild.get_channel(int(channel_ctx.id))
            if not channel_to_join or not isinstance(channel_to_join, discord.VoiceChannel):
                logging.warning(f"Channel {channel_ctx.id} is invalid")
                return
            
            # Working around a Pycord limitation that doesn't ensure proper voice client connection state
            # after the bot is moved to another channel
            # Always force a disconnect before reconnecting if we're already connected to a voice channel 
            if guild.voice_client:
                try:
                    if guild.voice_client.recording:
                        guild.voice_client.stop_recording()
                    await guild.voice_client.disconnect(force=True)
                except Exception as e:
                    logging.warning(f"Error disconnecting before reconnect: {e}")

            await asyncio.sleep(0.5)

            await channel_to_join.connect()

            if guild.voice_client:
                if not guild.voice_client.recording:
                    guild.voice_client.start_recording(
                        GrpcVadAudioSink(guild, self.audio_stream, self.vad, self.state),
                        self._recording_finished_callback
                    )
                    logging.info(f"Started recording in Channel {channel_to_join.id}")

            self.event_stream.push_session_state_update(guild, brain_pb2.SessionUpdate.ChangeType.STARTED, channel_to_join)

            self.event_stream.push_channel_snapshot(
                guild=guild,
                channel=channel_to_join,
                members=[m for m in channel_to_join.members if m.id != self.bot.user.id] # type: ignore
            )

            await self.state.notify_state_change(
                guild=guild,
                channel=channel_to_join,
                reason=brain_pb2.VoiceStateReason.CONNECT
            )

            await self.response.complete(correlation_id, success=True, title="Connected", description=f"Joined {channel_to_join.mention}")
        
        except Exception as e:
            self.state.consume_intent(guild_ctx.id)
            self.state.clear_session_id(guild_ctx.id)
            logging.error(f"Error handling execute_connect: {e}")
            await self.response.complete(correlation_id, success=False, title="Connection failed", description=str(e))
            raise
    
    async def handle_unstable_disconnect(self, guild: discord.Guild):
        logging.warning(f"Panic: Unstable voice state detected in guild {guild.id}. disconnecting.")
        
        # Register a DISCONNECT intent so StateManager doesn't freak out when we leave
        self.state.register_intent(guild.id, VoiceTransitionType.DISCONNECT)

        # Aggressively kill the Discord connection
        if guild.voice_client:
            try:
                # Suppress "Not recording" errors
                if guild.voice_client.recording: # type: ignore
                    guild.voice_client.stop_recording() # type: ignore
            except Exception:
                pass
            
            try:
                await guild.voice_client.disconnect(force=True)
            except Exception as e:
                logging.warning(f"Error checking out during unstable disconnect: {e}")

        self.event_stream.push_session_state_update(
            guild, 
            brain_pb2.SessionUpdate.ChangeType.UNSTABLE, 
            None 
        )

        await self.state.notify_state_change(
            guild=guild,
            channel=None,
            reason=brain_pb2.VoiceStateReason.MANUAL_DISCONNECT
        )
        
        self.state.consume_intent(guild.id)
        self.state.clear_session_id(guild.id)

    async def execute_disconnect(self, guild_ctx: brain_pb2.GuildContext, correlation_id: str | None, session_id: str | None = None):
        if not self.bot:
            raise RuntimeError("Bot not set")
        
        self.state.register_intent(guild_ctx.id, VoiceTransitionType.DISCONNECT)
        
        try:
            guild = self.bot.get_guild(int(guild_ctx.id))
            if not guild:
                raise ValueError(f"Guild {guild_ctx.id} does not exist")
            
            if guild.voice_client:
                if guild.voice_client.recording:
                    guild.voice_client.stop_recording()
                await guild.voice_client.disconnect(force=True)

            self.event_stream.push_session_state_update(guild, brain_pb2.SessionUpdate.ChangeType.ENDED, None)

            await self.state.notify_state_change(
                guild=guild,
                channel=None,
                reason=brain_pb2.VoiceStateReason.DISCONNECT,
            )

            self.state.clear_session_id(guild.id)

            await self.response.complete(correlation_id, success=True, title="Disconnected", description=f"Left the voice channel")
        except Exception:
            self.state.consume_intent(guild_ctx.id)
            raise

    async def _recording_finished_callback(self, sink, *args):
        logging.info("Recording finished")
