import logging
import discord
from discord.ext import commands

from generated.brain_pb2_grpc import BrainStub
from generated.brain_pb2 import JoinChannelRequest, LeaveChannelRequest

class VoiceCog(commands.Cog):
    def __init__(self, bot: discord.Bot, brain_stub: BrainStub, node_id: str):
        self.bot = bot
        self.brain = brain_stub
        self.node_id = node_id

    @discord.slash_command(name="join", description="Join the voice channel you are currently in")
    async def join(self, ctx: discord.ApplicationContext):
        if not ctx.author.voice or not ctx.author.voice.channel:
            await ctx.respond("You are not in a voice channel!", ephemeral=True)
            return

        channel_to_join = ctx.author.voice.channel
        await ctx.defer()

        try:
            req = JoinChannelRequest(guild_id=str(ctx.guild_id), channel_id=str(channel_to_join.id), node_id=self.node_id)
            res = self.brain.JoinChannel(req)

            if res.success:
                if res.instruction == 1: # CONNECT
                    if ctx.voice_client:
                        await ctx.voice_client.move_to(channel_to_join)
                    else:
                        await channel_to_join.connect()

                    await ctx.respond(f"Joined {channel_to_join.mention}")

                elif res.instruction == 0: # STAY
                    await ctx.respond("We're already in the same voice channel!")
            else:
                await ctx.respond(f"Cannot join: {res.message}", ephemeral=True)
        
        except Exception as e:
            logging.error(f"Failed to join VC: {e}")
            await ctx.respond("Something went wrong contacting the Brain.", ephemeral=True)

    @discord.slash_command(name="leave", description="Disconnect from the voice channel")
    async def leave(self, ctx: discord.ApplicationContext):

        if not ctx.voice_client:
            await ctx.respond("I'm not connected to any voice channel.", ephemeral=True)
            return
        
        try:
            req = LeaveChannelRequest(guild_id=str(ctx.guild_id), node_id=self.node_id)
            self.brain.LeaveChannel(req)
        except Exception as e:
            logging.error(f"Error notifying Brain of leave: {e}")

        await ctx.voice_client.disconnect()
        await ctx.respond(f"Bye!")

