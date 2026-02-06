import logging
import discord

class ResponseService:
    async def respond_working(self, ctx: discord.ApplicationContext):
        await ctx.defer()

    async def respond_success(self, ctx: discord.ApplicationContext, title: str, description: str):
        embed = discord.Embed(
            title=title,
            description=description,
            color=discord.Color.green()
        )
        await self._update_interaction(ctx, embed)

    async def respond_error(self, ctx: discord.ApplicationContext, message: str):
        embed = discord.Embed(
            title="Error",
            description=message,
            color=discord.Color.red()
        )
        await self._update_interaction(ctx, embed)

    async def _update_interaction(self, ctx: discord.ApplicationContext, embed: discord.Embed):
        try:
            await ctx.interaction.edit_original_response(content=None, embed=embed)
        except Exception as e:
            logging.error(f"Failed to edit interaction response: {e}")
            try:
                await ctx.send(embed=embed)
            except:
                pass # Something weird happened - token may have expired
        