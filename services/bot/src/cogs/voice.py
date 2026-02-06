import asyncio
import logging
import discord
from discord.ext import commands

from services.voice import VoiceService

class VoiceCog(commands.Cog):
    def __init__(self, bot: discord.Bot, voice_service: VoiceService, node_id: str):
        self.bot = bot
        self.voice = voice_service
        self.node_id = node_id

    async def _perform_connect(self, ctx, channel_to_join):
        try:
            if ctx.voice_client:
                await ctx.voice_client.move_to(channel_to_join)
            else:
                await channel_to_join.connect()
            await ctx.respond(f"Joined {channel_to_join.mention}")
        except Exception as e:
            logging.warning(f"Standard join failed ({e}) -- attempting hard reconnect")
            try:
                if ctx.voice_client:
                    await ctx.voice_client.disconnect(force=True)
                    await asyncio.sleep(0.5)
                await channel_to_join.connect()
                await ctx.respond(f"Joined {channel_to_join.mention}")
            except Exception as e2:
                logging.error(f"Hard reconnect failed: {e2}")
                await ctx.respond("Failed to connect to voice.", ephemeral=True)

    @discord.slash_command(name="join", description="Join the voice channel you are currently in")
    async def join(self, ctx: discord.ApplicationContext):
        if not ctx.author.voice or not ctx.author.voice.channel: # type: ignore
            await ctx.respond("You are not in a voice channel!", ephemeral=True)
            return

        channel_to_join = ctx.author.voice.channel # type: ignore
        await ctx.defer()

        try:
            success, instruction, message = self.voice.request_join(
                guild_id=str(ctx.guild_id), 
                channel_id=str(channel_to_join.id), 
                node_id=self.node_id
            )

            if success:
                if instruction == 1: # CONNECT
                    await self._perform_connect(ctx, channel_to_join)
                elif instruction == 0: # STAY
                    await ctx.respond("We're already in the same voice channel!")
            else:
                await ctx.respond(f"Cannot join: {message}", ephemeral=True)
        
        except Exception as e:
            logging.error(f"Failed to join VC: {e}")
            await ctx.respond("Something went wrong contacting the Brain.", ephemeral=True)

    @discord.slash_command(name="leave", description="Disconnect from the voice channel")
    async def leave(self, ctx: discord.ApplicationContext):
        if not ctx.voice_client:
            await ctx.respond("I'm not connected to any voice channel.", ephemeral=True)
            return
        
        self.voice.notify_leave(str(ctx.guild_id), self.node_id)

        await ctx.voice_client.disconnect()
        await ctx.respond(f"Bye!")

