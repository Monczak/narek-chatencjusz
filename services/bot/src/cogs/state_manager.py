import asyncio
import logging
import socket
import discord
from discord.ext import commands, tasks

from generated import brain_pb2
from services.event_stream import EventStreamService
from services.state import StateService, VoiceTransitionType

class StateManager(commands.Cog):
    def __init__(self, bot: discord.Bot, state_service: StateService, event_stream: EventStreamService):
        self.bot = bot
        self.state = state_service
        self.event_stream = event_stream
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
                logging.warning(f"Healing: I am in guild {guild_id} but Valkey didn't know -- reconciling state")
                current_channel_id = str(vc.channel.id) # type: ignore
                await self.state.notify_state_change(
                    gid,
                    current_channel_id,
                    brain_pb2.VoiceStateReason.RECONCILE_MISSING
                )

            elif registered_node != self.state.node_id:
                logging.warning(f"Conflict: I am in guild {guild_id} but Valkey thinks {registered_node} is there -- disconnecting")
                await vc.disconnect(force=True)
                continue
            
            current_channel_id = str(vc.channel.id) # type: ignore
            registered_channel = self.state.get_registered_channel(gid)

            if registered_channel != current_channel_id: # type: ignore
                logging.warning(f"Reconciling: Channel mismatch in guild {guild_id}. Valkey: {registered_channel} -> Real: {vc.channel.id}") # type: ignore
                await self.state.notify_state_change(
                    gid, 
                    current_channel_id, 
                    brain_pb2.VoiceStateReason.RECONCILE_DRIFT
                )

    async def _handle_bot_voice_update(self, member: discord.Member, before: discord.VoiceState, after: discord.VoiceState):
        if before.channel == after.channel:
            return
        
        gid = str(member.guild.id)
        intent = self.state.get_intent(gid)

        # --- Disconnection ---
        if after.channel is None:
            # Did we intend to disconnect?
            if intent and intent.type == VoiceTransitionType.DISCONNECT:
                # All good
                self.state.consume_intent(gid)
                return
            
            # Did we get a spurious disconnect signal because the earlier voice connection died?
            if member.guild.voice_client:
                await asyncio.sleep(1.0) # Debounce
                vc = member.guild.voice_client

                if vc and vc.is_connected():
                    logging.warning(f"Ignoring spurious disconnect in guild {gid}")
                    return # Spurious disconnect
            
            # Otherwise it's a real disconnect
            logging.warning(f"Detected manual disconnect in guild {gid} -- reconciling state")
            await self.state.notify_state_change(
                gid, 
                None, 
                brain_pb2.VoiceStateReason.MANUAL_DISCONNECT
            )
            self.event_stream.push_session_state_update(
                guild_id=gid,
                change_type=brain_pb2.SessionUpdate.ChangeType.ENDED
            )
            return
        
        # --- Moving or joining ---
        if after.channel is not None:
            current_channel_id = str(after.channel.id)
            
            # Did we intend to go here?
            if intent and intent.type == VoiceTransitionType.CONNECT:
                if intent.target_channel_id == current_channel_id:
                    # All good
                    self.state.consume_intent(gid)
                    return
                else:
                    logging.warning(f"Bot landed in channel {current_channel_id} but meant to go to {intent.target_channel_id}")
                    self.state.consume_intent(gid)
                    return
            
            # Are we where we should be?
            registered_channel = self.state.get_registered_channel(gid)
            if registered_channel and registered_channel == current_channel_id:
                logging.warning(f"Ignoring spurious move in guild {gid}")
                return # Ignore spurious move event

            # We are somewhere else - might have been moved manually
            logging.info(f"Detected move to new channel {after.channel.id} -- reconciling state")
            await self.state.notify_state_change(
                gid, 
                current_channel_id, 
                brain_pb2.VoiceStateReason.MANUAL_MOVE
            )

    async def _handle_user_voice_update(self, member: discord.Member, before: discord.VoiceState, after: discord.VoiceState):
        if not member.guild.voice_client or not member.guild.voice_client.channel:
            return
        
        bot_channel_id = member.guild.voice_client.channel.id # type: ignore
        gid = str(member.guild.id)

        # User joined bot's channel
        if after.channel and after.channel.id == bot_channel_id and (not before.channel or before.channel.id != bot_channel_id):
            self.event_stream.push_user_state_update(
                guild_id=gid,
                user=member,
                channel_id=str(bot_channel_id),
                change_type=brain_pb2.UserVoiceStateUpdate.JOINED
            )
        
        # User left bot's channel
        elif before.channel and before.channel.id == bot_channel_id and (not after.channel or after.channel.id != bot_channel_id):
            self.event_stream.push_user_state_update(
                guild_id=gid,
                user=member,
                channel_id=str(bot_channel_id),
                change_type=brain_pb2.UserVoiceStateUpdate.LEFT
            )

    @commands.Cog.listener()
    async def on_voice_state_update(self, member: discord.Member, before: discord.VoiceState, after: discord.VoiceState):        
        # Does this apply to us, the bot?
        if member.id == self.bot.user.id: # type: ignore
            await self._handle_bot_voice_update(member, before, after)
        else:
            await self._handle_user_voice_update(member, before, after)
        
        
        
