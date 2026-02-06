import asyncio
import discord
import logging
from typing import Optional, Tuple
from generated import brain_pb2, brain_pb2_grpc

class VoiceService:
    def __init__(self, brain: brain_pb2_grpc.BrainStub) -> None:
        self.brain = brain
        self.bot: discord.Bot | None = None # Injected later

    def set_bot(self, bot: discord.Bot):
        self.bot = bot

    def request_join(self, guild_id: str, channel_id: str, node_id: str) -> Tuple[bool, str]:
        try:
            req = brain_pb2.JoinChannelRequest(
                guild_id=guild_id,
                channel_id=channel_id,
                node_id=node_id
            )
            res = self.brain.JoinChannel(req)
            return res.success, res.message
        except Exception as e:
            logging.error(f"Brain voice join error: {e}")
            raise
    
    def request_leave(self, guild_id: str, node_id: str) -> None:
        try:
            req = brain_pb2.LeaveChannelRequest(guild_id=guild_id, node_id=node_id)
            self.brain.LeaveChannel(req)
        except Exception as e:
            logging.error(f"Brain voice leave error: {e}")
    
    async def execute_connect(self, guild_id: str, channel_id: str):
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
        
        except Exception as e:
            logging.error(f"Error handling execute_connect: {e}")
    
    async def execute_disconnect(self, guild_id: str):
        if not self.bot:
            raise RuntimeError("Bot not set")
        
        guild = self.bot.get_guild(int(guild_id))
        if guild and guild.voice_client:
            await guild.voice_client.disconnect()
