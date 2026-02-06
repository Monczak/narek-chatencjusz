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

    @discord.slash_command(name="join", description="Join the voice channel you are currently in")
    async def join(self, ctx: discord.ApplicationContext):
        if not ctx.author.voice or not ctx.author.voice.channel: # type: ignore
            await ctx.respond("You are not in a voice channel!", ephemeral=True)
            return

        channel_to_join = ctx.author.voice.channel # type: ignore

        await self.response.respond_working(ctx)

        correlation_id = self.interaction.register(ctx)

        try:
            success, message = self.voice.request_join(
                guild_id=str(ctx.guild_id),
                channel_id=str(channel_to_join.id),
                node_id=self.node_id,
                correlation_id=correlation_id
            )

            if not success:
                self.interaction.discard(correlation_id)
                await self.response.respond_error(ctx, f"Brain denied request: {message}")
        
        except Exception as e:
            logging.error(f"Failed to join VC: {e}")
            self.interaction.discard(correlation_id)
            await self.response.respond_error(ctx, "Something went wrong contacting the Brain.")

    @discord.slash_command(name="leave", description="Disconnect from the voice channel")
    async def leave(self, ctx: discord.ApplicationContext):
        if not ctx.voice_client:
            await ctx.respond("I'm not connected to any voice channel.", ephemeral=True)
            return
        
        await self.response.respond_working(ctx)
        correlation_id = self.interaction.register(ctx)

        try:
            self.voice.request_leave(
                guild_id=str(ctx.guild_id), 
                node_id=self.node_id,
                correlation_id=correlation_id
            )
        except Exception as e:
            logging.error(f"Failed to leave VC: {e}")
            self.interaction.discard(correlation_id)
            await self.response.respond_error(ctx, "Something went wrong contacting the Brain.")
            
