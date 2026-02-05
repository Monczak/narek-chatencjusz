import discord
from discord.ext import commands

from infra.brain import BrainClient

class UtilCog(commands.Cog):
    def __init__(self, bot: discord.Bot, brain_client: BrainClient):
        self.bot = bot
        self.brain = brain_client

    @commands.slash_command(name="ping", description="Ping")
    async def ping(self, ctx: discord.ApplicationContext):
        msg = self.brain.ping("Hello")
        await ctx.respond(f"Brain says: {msg}")
