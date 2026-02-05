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
