import asyncio
import logging
import discord
from discord.ext import commands

from services.interaction import InteractionService
from services.response import ResponseService
from services.voice import VoiceService

class VoiceCog(commands.Cog):
    def __init__(
        self, 
        bot: discord.Bot, 
        voice_service: VoiceService, 
        response_service: ResponseService,
        interaction_service: InteractionService,
        node_id: str
    ):
        self.bot = bot
        self.voice = voice_service
        self.response = response_service
        self.interaction = interaction_service
        self.node_id = node_id

    @commands.slash_command(name="join", description="Join the voice channel you are currently in")
    async def join(self, ctx: discord.ApplicationContext):
        if not ctx.author.voice or not ctx.author.voice.channel: # type: ignore
            await self.response.respond_error(ctx, "You are not in a voice channel!", ephemeral=True)
            return

        channel_to_join = ctx.author.voice.channel # type: ignore

        await self.response.respond_working(ctx)
        
        with self.interaction.long_interaction(ctx) as (correlation_id, handle):
            try:
                success, message = await self.voice.request_join(
                    guild=ctx.guild, # type: ignore
                    channel=channel_to_join, # type: ignore
                    node_id=self.node_id,
                    correlation_id=correlation_id
                )

                if success:
                    handle.keep()
                else:
                    await self.response.respond_error(ctx, f"Brain denied request: {message}")
            
            except Exception as e:
                logging.error(f"Failed to join VC: {e}")
                await self.response.respond_error(ctx, "Something went wrong contacting the Brain.")

    @commands.slash_command(name="leave", description="Disconnect from the voice channel")
    async def leave(self, ctx: discord.ApplicationContext):
        if not ctx.voice_client:
            await self.response.respond_error(ctx, "I'm not connected to any voice channel.", ephemeral=True)
            return
        
        await self.response.respond_working(ctx)
        
        with self.interaction.long_interaction(ctx) as (correlation_id, handle):
            try:
                success = await self.voice.request_leave(
                    guild=ctx.guild, # type: ignore
                    node_id=self.node_id,
                    correlation_id=correlation_id
                )

                if success:
                    handle.keep()

            except Exception as e:
                logging.error(f"Failed to leave VC: {e}")
                await self.response.respond_error(ctx, "Something went wrong contacting the Brain.")
            
