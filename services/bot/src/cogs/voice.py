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

    @discord.slash_command(name="join", description="Join the voice channel you are currently in")
    async def join(self, ctx: discord.ApplicationContext):
        if not ctx.author.voice or not ctx.author.voice.channel: # type: ignore
            await ctx.respond("You are not in a voice channel!", ephemeral=True)
            return

        channel_to_join = ctx.author.voice.channel # type: ignore
        await ctx.defer()

        try:
            success, message = self.voice.request_join(
                guild_id=str(ctx.guild_id),
                channel_id=str(channel_to_join.id),
                node_id=self.node_id
            )

            if success:
                await ctx.respond("Joining soon!", ephemeral=True)
            else:
                await ctx.respond(f"Brain denied request: {message}", ephemeral=True)
        
        except Exception as e:
            logging.error(f"Failed to join VC: {e}")
            await ctx.respond("Something went wrong contacting the Brain.", ephemeral=True)

    @discord.slash_command(name="leave", description="Disconnect from the voice channel")
    async def leave(self, ctx: discord.ApplicationContext):
        if not ctx.voice_client:
            await ctx.respond("I'm not connected to any voice channel.", ephemeral=True)
            return
        
        self.voice.request_leave(str(ctx.guild_id), self.node_id)
        await ctx.respond(f"Leaving soon!")
