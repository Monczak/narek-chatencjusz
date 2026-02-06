import logging
import discord

class ResponseService:
    async def respond_working(self, ctx: discord.ApplicationContext):
        await ctx.defer()

    async def respond_success(self, ctx: discord.ApplicationContext, title: str, description: str, ephemeral: bool = False):
        embed = discord.Embed(
            title=title,
            description=description,
            color=discord.Color.green()
        )
        await self._update_interaction(ctx, embed, ephemeral)

    async def respond_error(self, ctx: discord.ApplicationContext, message: str, ephemeral: bool = False):
        embed = discord.Embed(
            title="Error",
            description=message,
            color=discord.Color.red()
        )
        await self._update_interaction(ctx, embed, ephemeral)

    async def _update_interaction(self, ctx: discord.ApplicationContext, embed: discord.Embed, ephemeral: bool = False):
        try:
            if ctx.response.is_done():
                await ctx.interaction.edit_original_response(content=None, embed=embed)
            else:
                await ctx.respond(embed=embed, ephemeral=ephemeral)
        except Exception as e:
            logging.error(f"Failed to edit interaction response: {e}")
            try:
                # Fallback for weird states
                await ctx.send(embed=embed)
            except:
                pass
        