import logging
import socket
import discord
from discord.ext import commands, tasks

from valkey import Valkey

class StateManager(commands.Cog):
    def __init__(self, bot: discord.Bot, valkey_client: Valkey, node_id: str):
        self.bot = bot
        self.valkey = valkey_client
        self.node_id = node_id

        self.ip_address = socket.gethostbyname(socket.gethostname())

        self.heartbeat.start()
        self.reconcile_state.start()

    def _get_node_key(self):
        return f"node:{self.node_id}:heartbeat"

    def cog_unload(self):
        self.heartbeat.cancel()
        self.reconcile_state.cancel()
        self.valkey.delete(self._get_node_key())

    @tasks.loop(seconds=3.0)
    async def heartbeat(self):
        data = {"ip": self.ip_address, "load": len(self.bot.voice_clients)}
        self.valkey.set(self._get_node_key(), str(data), ex=5)

    @tasks.loop(seconds=5.0)
    async def reconcile_state(self):
        connected_guilds = {vc.guild.id: vc for vc in self.bot.voice_clients if vc.is_connected()} # type: ignore

        for guild_id, vc in connected_guilds.items():
            key = f"guild:{guild_id}:connection"
            registered_node = self.valkey.get(key)

            if registered_node is None:
                logging.warning(f"Healing: I am in guild {guild_id} but Valkey didn't know -- registering session")
                self.valkey.set(key, self.node_id)
            elif registered_node != self.node_id:
                logging.warning(f"Conflict: I am in guild {guild_id} but Valkey thinks {registered_node} is there -- disconnecting")
                await vc.disconnect(force=True)
            
            channel_key = f"guild:{guild_id}:channel"
            registered_channel = self.valkey.get(channel_key)

            if registered_channel != str(vc.channel.id): # type: ignore
                logging.warning(f"Reconciling: Channel mismatch in guild {guild_id}. Valkey: {registered_channel} -> Real: {vc.channel.id}") # type: ignore
                self.valkey.set(channel_key, str(vc.channel.id)) # type: ignore

    @commands.Cog.listener()
    async def on_voice_state_update(self, member: discord.Member, before: discord.VoiceState, after: discord.VoiceState):
        if member.id != self.bot.user.id: # type: ignore
            return
        
        if before.channel == after.channel:
            return
        
        guild_id = member.guild.id

        # Manual disconnect
        if after.channel is None:
            expected_channel_id = self.valkey.get(f"guild:{guild_id}:channel")
            
            if expected_channel_id and before.channel and expected_channel_id != before.channel.id:
                # Move in progress - ignore
                return
            
            # Otherwise it's a real disconnect
            logging.warning(f"Detected manual disconnect in guild {guild_id} -- cleaning Valkey")
            self.valkey.delete(f"guild:{guild_id}:connection")
            self.valkey.delete(f"guild:{guild_id}:channel")
            return
        
        # Moved to another channel
        if after.channel is not None:
            current_channel_id = str(after.channel.id)
            expected_channel_id = self.valkey.get(f"guild:{guild_id}:channel")

            if expected_channel_id == current_channel_id:
                # We are where the state says we should be - all good
                return
            
            # We are somewhere else - might have been moved manually
            logging.info(f"Detected move to new channel {after.channel.id} -- updating Valkey")
            self.valkey.set(f"guild:{guild_id}:channel", str(after.channel.id))
            self.valkey.set(f"guild:{guild_id}:connection", self.node_id)
