import logging
import discord
from discord.ext import commands

from services.config import ConfigService
from services.response import ResponseService
from generated import brain_pb2

class ConfigCog(commands.Cog):
    config_group = discord.SlashCommandGroup("config",  "Configure Narek Chatencjusz for this server")
    prompt_group = config_group.create_subgroup("prompt",  "Manage the system prompt / character card")
    inst_group = config_group.create_subgroup("instructions", "Manage custom instructions appended to the prompt")
    ramble_group = config_group.create_subgroup("ramble", "Configure ramble (unprompted speech) mode")

    def __init__(self, bot: discord.Bot, config_service: ConfigService, response_service: ResponseService) -> None:
        self.bot = bot
        self.config = config_service
        self.response = response_service

    # Show

    @config_group.command(name="show", description="Show the current LLM configuration for this server")
    async def config_show(self, ctx: discord.ApplicationContext) -> None:
        await ctx.defer(ephemeral=True)
        try:
            resp = await self.config.get_settings(ctx.guild_id)  # type: ignore
            embed = _settings_embed(resp)
            await ctx.interaction.edit_original_response(embed=embed)
        except Exception as e:
            logging.error("config show failed: %s", e)
            await self.response.respond_error(ctx, "Failed to fetch settings from Brain.", ephemeral=True)

    # Name

    @config_group.command(name="name", description="Override the bot's name as seen by the LLM")
    async def config_name(
        self,
        ctx: discord.ApplicationContext,
        name: discord.Option(str, "New name (leave blank to reset to default)", required=True),  # type: ignore
    ) -> None:
        await ctx.defer(ephemeral=True)
        try:
            await self.config.set_bot_name(ctx.guild_id, name)  # type: ignore
            await self.response.respond_success(
                ctx, 
                "Bot name updated", 
                f"Bot name set to **{name}**.", 
                ephemeral=True
            )
        except Exception as e:
            logging.error("config name failed: %s", e)
            await self.response.respond_error(ctx, "Failed to update settings.", ephemeral=True)

    # Temperature

    @config_group.command(name="temperature", description="Set the LLM sampling temperature (0.0 - 2.0)")
    async def config_temperature(
        self,
        ctx: discord.ApplicationContext,
        value: discord.Option(float, "Temperature value between 0.0 and 2.0", min_value=0.0, max_value=2.0),  # type: ignore
    ) -> None:
        await ctx.defer(ephemeral=True)
        try:
            await self.config.set_temperature(ctx.guild_id, value)  # type: ignore
            await self.response.respond_success(
                ctx, 
                "Temperature updated", 
                f"Temperature set to **{value}**.", 
                ephemeral=True
            )
        except Exception as e:
            logging.error("config temperature failed: %s", e)
            await self.response.respond_error(ctx, "Failed to update settings.", ephemeral=True)

    # Silence threshold

    @config_group.command(name="silence-threshold", description="Seconds of silence before the bot responds (0.5 - 10)")
    async def config_silence(
        self,
        ctx: discord.ApplicationContext,
        seconds: discord.Option(float, "Silence threshold in seconds", min_value=0.5, max_value=10.0),  # type: ignore
    ) -> None:
        await ctx.defer(ephemeral=True)
        try:
            ms = int(seconds * 1000)
            await self.config.set_silence_threshold(ctx.guild_id, ms)  # type: ignore
            await self.response.respond_success(
                ctx, 
                "Silence threshold updated", 
                f"Silence threshold set to **{seconds}s** ({ms} ms).", 
                ephemeral=True
            )
        except Exception as e:
            logging.error("config silence-threshold failed: %s", e)
            await self.response.respond_error(ctx, "Failed to update settings.", ephemeral=True)

    # Prompt

    @prompt_group.command(name="set", description="Set a custom system prompt / character card for this server")
    async def prompt_set(
        self,
        ctx: discord.ApplicationContext,
        text: discord.Option(str, "The full system prompt text"),  # type: ignore
    ) -> None:
        await ctx.defer(ephemeral=True)
        try:
            await self.config.set_system_prompt(ctx.guild_id, text)  # type: ignore
            preview = text[:120] + "..." if len(text) > 120 else text
            await self.response.respond_success(
                ctx, 
                "System prompt updated", 
                f"> {preview}", 
                ephemeral=True
            )
        except Exception as e:
            logging.error("prompt set failed: %s", e)
            await self.response.respond_error(ctx, "Failed to update system prompt.", ephemeral=True)

    @prompt_group.command(name="clear", description="Remove the custom system prompt and revert to the server default")
    async def prompt_clear(self, ctx: discord.ApplicationContext) -> None:
        await ctx.defer(ephemeral=True)
        try:
            await self.config.clear_system_prompt(ctx.guild_id)  # type: ignore
            await self.response.respond_success(
                ctx, 
                "System prompt cleared", 
                "Using server default system prompt.", 
                ephemeral=True
            )
        except Exception as e:
            logging.error("prompt clear failed: %s", e)
            await self.response.respond_error(ctx, "Failed to clear system prompt.", ephemeral=True)

    # Custom instructions

    @inst_group.command(name="set", description="Set custom instructions appended after the system prompt")
    async def instructions_set(
        self,
        ctx: discord.ApplicationContext,
        text: discord.Option(str, "The instructions text"),  # type: ignore
    ) -> None:
        await ctx.defer(ephemeral=True)
        try:
            await self.config.set_custom_instructions(ctx.guild_id, text)  # type: ignore
            await self.response.respond_success(
                ctx, 
                "Custom instructions updated", 
                "New instructions have been saved.", 
                ephemeral=True
            )
        except Exception as e:
            logging.error("instructions set failed: %s", e)
            await self.response.respond_error(ctx, "Failed to update custom instructions.", ephemeral=True)

    @inst_group.command(name="clear", description="Remove the custom instructions for this server")
    async def instructions_clear(self, ctx: discord.ApplicationContext) -> None:
        await ctx.defer(ephemeral=True)
        try:
            await self.config.clear_custom_instructions(ctx.guild_id)  # type: ignore
            await self.response.respond_success(
                ctx, 
                "Custom instructions cleared", 
                "Instructions removed.", 
                ephemeral=True
            )
        except Exception as e:
            logging.error("instructions clear failed: %s", e)
            await self.response.respond_error(ctx, "Failed to clear custom instructions.", ephemeral=True)

    # Ramble

    @ramble_group.command(name="toggle", description="Enable or disable ramble mode (bot speaks unprompted during silences)")
    async def ramble_toggle(
        self,
        ctx: discord.ApplicationContext,
        enabled: discord.Option(bool, "True to enable, False to disable"),  # type: ignore
    ) -> None:
        await ctx.defer(ephemeral=True)
        try:
            await self.config.set_ramble_enabled(ctx.guild_id, enabled)  # type: ignore
            state = "enabled" if enabled else "disabled"
            await self.response.respond_success(
                ctx, 
                "Ramble mode updated", 
                f"Ramble mode **{state}**.", 
                ephemeral=True
            )
        except Exception as e:
            logging.error("ramble toggle failed: %s", e)
            await self.response.respond_error(ctx, "Failed to update ramble mode.", ephemeral=True)

    @ramble_group.command(name="threshold", description="Seconds of silence before the bot speaks unprompted (10 - 600)")
    async def ramble_threshold(
        self,
        ctx: discord.ApplicationContext,
        seconds: discord.Option(int, "Threshold in seconds", min_value=10, max_value=600),  # type: ignore
    ) -> None:
        await ctx.defer(ephemeral=True)
        try:
            await self.config.set_ramble_threshold(ctx.guild_id, seconds)  # type: ignore
            await self.response.respond_success(
                ctx, 
                "Ramble threshold updated", 
                f"Ramble threshold set to **{seconds}s**.", 
                ephemeral=True
            )
        except Exception as e:
            logging.error("ramble threshold failed: %s", e)
            await self.response.respond_error(ctx, "Failed to update ramble threshold.", ephemeral=True)


def _settings_embed(resp: brain_pb2.GetGuildSettingsResponse) -> discord.Embed:
    r = resp.resolved
    o = resp.overrides

    def _marker(field_name: str) -> str:
        return " *" if o.HasField(field_name) else "" # type: ignore

    def _trunc(text: str, n: int = 300) -> str:
        return text[:n] + "..." if len(text) > n else text

    embed = discord.Embed(
        title="Guild LLM Configuration",
        description="* = overridden for this server  |  no mark = using server default",
        color=discord.Color.blurple(),
    )

    embed.add_field(
        name=f"Bot Name{_marker("bot_name")}",
        value=r.bot_name or "-",
        inline=True,
    )
    embed.add_field(
        name=f"Temperature{_marker("temperature")}",
        value=str(round(r.temperature, 2)),
        inline=True,
    )
    embed.add_field(
        name=f"Silence Threshold{_marker("silence_threshold_ms")}",
        value=f"{r.silence_threshold_ms} ms",
        inline=True,
    )
    embed.add_field(
        name=f"Ramble Mode{_marker("ramble_mode_enabled")}",
        value="Enabled" if r.ramble_mode_enabled else "Disabled",
        inline=True,
    )
    embed.add_field(
        name=f"Ramble Threshold{_marker("ramble_threshold_ms")}",
        value=f"{r.ramble_threshold_ms // 1000}s",
        inline=True,
    )
    embed.add_field(
        name=f"Time Zone{_marker("time_zone")}",
        value=r.time_zone or "UTC",
        inline=True,
    )

    if r.system_prompt:
        label = f"System Prompt{_marker("system_prompt")}"
        embed.add_field(name=label, value=_trunc(r.system_prompt), inline=False)

    if r.custom_instructions:
        label = f"Custom Instructions{_marker("custom_instructions")}"
        embed.add_field(name=label, value=_trunc(r.custom_instructions), inline=False)

    return embed
