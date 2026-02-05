import discord
from discord.ext import commands

from services.util import UtilService

class UtilCog(commands.Cog):
    def __init__(self, bot: discord.Bot, util_service: UtilService):
        self.bot = bot
        self.util_service = util_service

    @commands.slash_command(name="ping", description="Ping")
    async def ping(self, ctx: discord.ApplicationContext):
        response = self.util_service.ping("Hello")
        await ctx.respond(response)

    @commands.slash_command(name="sync", description="Force sync slash commands")
    @commands.is_owner()
    async def sync(self, ctx: discord.ApplicationContext):
        await ctx.defer()
        await self.bot.sync_commands(force=True)
        await ctx.respond("Commands synced")
