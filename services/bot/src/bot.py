from typing import Awaitable, Callable, List
import discord
import logging

from valkey import Valkey

from services.audio_stream import AudioStreamService
from services.bot_config import BotConfigService
from services.event_stream import EventStreamService
from services.messaging import CommandListener
from services.voice import VoiceService
from services.voice_keepalive import VoiceKeepaliveService

class NarekChatencjuszBot(discord.Bot):
    def __init__(
        self, 
        node_id: str, 
        version: str,
        valkey_client: Valkey,
        util_cog_factory: Callable[..., Awaitable[discord.Cog]],
        state_cog_factory: Callable[..., Awaitable[discord.Cog]],
        voice_cog_factory: Callable[..., Awaitable[discord.Cog]],
        config_cog_factory: Callable[..., Awaitable[discord.Cog]],
        debug_guild_ids: List[int],
        voice_service: VoiceService,
        command_listener: CommandListener,
        event_stream: EventStreamService,
        audio_stream: AudioStreamService,
        keepalive_service: VoiceKeepaliveService
    ):
        intents = discord.Intents.default()
        intents.members = True

        super().__init__(
            debug_guilds=debug_guild_ids,
            intents=intents
        )

        self.node_id = node_id
        self.version = version
        self.valkey = valkey_client

        self.util_cog_factory = util_cog_factory
        self.state_cog_factory = state_cog_factory
        self.voice_cog_factory = voice_cog_factory
        self.config_cog_factory = config_cog_factory

        self.voice_service = voice_service
        self.command_listener = command_listener
        self.event_stream = event_stream
        self.audio_stream = audio_stream
        self.keepalive_service = keepalive_service

    async def setup_cogs(self):
        logging.info("Loading cogs...")

        self.add_cog(await self.util_cog_factory(bot=self))
        self.add_cog(await self.state_cog_factory(bot=self))
        self.add_cog(await self.voice_cog_factory(bot=self))
        self.add_cog(await self.config_cog_factory(bot=self))

    async def on_ready(self):
        logging.info(f"Logged in as {self.user}")

        self.voice_service.set_bot(self)
        logging.info("VoiceService linked to bot instance")

        bot_config: BotConfigService = self.command_listener.bot_config
        await bot_config.refresh()
        logging.info("BotConfigService initialized")

        self.loop.create_task(self.command_listener.start())
        logging.info("CommandListener started")

        await self.event_stream.start()
        logging.info("EventStreamService started")

        await self.audio_stream.start()
        logging.info("AudioStreamService started")

        await self.keepalive_service.start()
        logging.info("VoiceKeepaliveService started")

        logging.info(f"Narek Chatencjusz bot service is up and running")

    async def close(self):
        logging.info(f"Gracefully shutting down -- cleaning up voice connections...")

        if self.keepalive_service:
            await self.keepalive_service.stop()

        if self.event_stream:
            await self.event_stream.stop()

        if self.audio_stream:
            await self.audio_stream.stop()

        try:
            if self.voice_clients:
                for vp in self.voice_clients:
                    vc: discord.VoiceClient = vp # type: ignore
                    try:
                        guild_id = vc.guild.id # type: ignore
                        logging.info(f"Disconnecting from guild {guild_id}")

                        self.valkey.delete(f"guild:{guild_id}:connection")
                        self.valkey.delete(f"guild:{guild_id}:channel")

                        if vc.is_recording():
                            vc.stop_recording()
                        
                        if vc.is_connected():
                            await vc.disconnect(force=True)
                    except Exception as e:
                        logging.error(f"Error disconnecting from guild {vc.guild.id}: {e}") # type: ignore
            
            self.valkey.delete(f"node:{self.node_id}:heartbeat")
        
        except Exception as e:
            logging.error(f"Error during graceful shutdown: {e}")
        
        await super().close()
