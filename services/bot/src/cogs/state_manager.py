import logging
import socket
import discord
from discord.ext import commands, tasks

from services.state import StateService

class StateManager(commands.Cog):
    def __init__(self, bot: discord.Bot, state_service: StateService):
        self.bot = bot
        self.state = state_service
        self.ip_address = socket.gethostbyname(socket.gethostname())

        self.heartbeat.start()
        self.reconcile_state.start()

    def cog_unload(self):
        self.heartbeat.cancel()
        self.reconcile_state.cancel()
        self.state.remove_node_heartbeat()

    @tasks.loop(seconds=3.0)
    async def heartbeat(self):
        load = len(self.bot.voice_clients)
        self.state.report_heartbeat(self.ip_address, load)

    @tasks.loop(seconds=5.0)
    async def reconcile_state(self):
        connected_guilds = {vc.guild.id: vc for vc in self.bot.voice_clients if vc.is_connected()} # type: ignore

        for guild_id, vc in connected_guilds.items():
            gid = str(guild_id)

            registered_node = self.state.get_registered_node(gid)

            if registered_node is None:
                logging.warning(f"Healing: I am in guild {guild_id} but Valkey didn't know -- registering session")
                self.state.register_guild_session(gid)
            elif registered_node != self.state.node_id:
                logging.warning(f"Conflict: I am in guild {guild_id} but Valkey thinks {registered_node} is there -- disconnecting")
                await vc.disconnect(force=True)
                continue
            
            current_channel_id = str(vc.channel.id) # type: ignore
            registered_channel = self.state.get_registered_channel(gid)

            if registered_channel != current_channel_id: # type: ignore
                logging.warning(f"Reconciling: Channel mismatch in guild {guild_id}. Valkey: {registered_channel} -> Real: {vc.channel.id}") # type: ignore
                self.state.register_guild_channel(gid, current_channel_id)

    @commands.Cog.listener()
    async def on_voice_state_update(self, member: discord.Member, before: discord.VoiceState, after: discord.VoiceState):
        if member.id != self.bot.user.id: # type: ignore
            return
        
        if before.channel == after.channel:
            return
        
        gid = str(member.guild.id)

        # Manual disconnect
        if after.channel is None:
            expected_channel_id = self.state.get_registered_channel(gid)
            
            if expected_channel_id and before.channel and expected_channel_id != before.channel.id:
                # Move in progress - ignore
                return
            
            # Otherwise it's a real disconnect
            logging.warning(f"Detected manual disconnect in guild {gid} -- cleaning Valkey")
            self.state.clear_guild_session(gid)
            return
        
        # Moved to another channel
        if after.channel is not None:
            current_channel_id = str(after.channel.id)
            expected_channel_id = self.state.get_registered_channel(gid)

            if expected_channel_id == current_channel_id:
                # We are where the state says we should be - all good
                return
            
            # We are somewhere else - might have been moved manually
            logging.info(f"Detected move to new channel {after.channel.id} -- updating Valkey")
            self.state.update_guild_session(gid, current_channel_id)
