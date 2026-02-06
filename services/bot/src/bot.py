from typing import Callable, List
import discord
import logging

from valkey import Valkey

class NarekChatencjuszBot(discord.Bot):
    def __init__(
        self, 
        node_id: str, 
        version: str,
        valkey_client: Valkey,
        util_cog_factory: Callable[..., discord.Cog],
        state_cog_factory: Callable[..., discord.Cog],
        voice_cog_factory: Callable[..., discord.Cog],
        debug_guild_ids: List[int]
    ):
        super().__init__(
            debug_guilds=debug_guild_ids
        )

        self.node_id = node_id
        self.version = version
        self.valkey = valkey_client

        self.util_cog_factory = util_cog_factory
        self.state_cog_factory = state_cog_factory
        self.voice_cog_factory = voice_cog_factory

    def setup_cogs(self):
        logging.info("Loading cogs...")

        self.add_cog(self.util_cog_factory(bot=self))
        self.add_cog(self.state_cog_factory(bot=self))
        self.add_cog(self.voice_cog_factory(bot=self))

    async def on_ready(self):
        logging.info(f"Logged in as {self.user}")
        logging.info(f"Narek Chatencjusz bot service is up and running")

    async def close(self):
        logging.info(f"Gracefully shutting down -- cleaning up voice connections...")

        try:
            if self.voice_clients:
                for vc in self.voice_clients:
                    try:
                        guild_id = vc.guild.id # type: ignore
                        logging.info(f"Disconnecting from guild {guild_id}")

                        self.valkey.delete(f"guild:{guild_id}:connection")
                        self.valkey.delete(f"guild:{guild_id}:channel")

                        await vc.disconnect(force=True)
                    except Exception as e:
                        logging.error(f"Error disconnecting from guild {vc.guild.id}: {e}") # type: ignore
            
            self.valkey.delete(f"node:{self.node_id}:heartbeat")
        
        except Exception as e:
            logging.error(f"Error during graceful shutdown: {e}")
        
        await super().close()

