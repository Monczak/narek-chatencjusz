import discord
import logging

from containers import Container

class NarekChatencjuszBot(discord.Bot):
    def __init__(self, container: Container, node_id: str, version: str):
        super().__init__(
            debug_guilds=container.config.debug_guild_ids()
        )

        self.container = container
        self.node_id = node_id
        self.version = version

    def setup_cogs(self):
        logging.info("Loading cogs...")

        self.add_cog(self.container.util_cog(bot=self))
        self.add_cog(self.container.state_cog(bot=self))
        self.add_cog(self.container.voice_cog(bot=self))

    async def on_ready(self):
        logging.info(f"Logged in as {self.user}")
        logging.info(f"Narek Chatencjusz bot service is up and running")

    async def close(self):
        logging.info(f"Gracefully shutting down -- cleaning up voice connections...")

        try:
            valkey = self.container.valkey_client()
            if self.voice_clients:
                for vc in self.voice_clients:
                    try:
                        guild_id = vc.guild.id # type: ignore
                        logging.info(f"Disconnecting from guild {guild_id}")

                        valkey.delete(f"guild:{guild_id}:connection")
                        valkey.delete(f"guild:{guild_id}:channel")

                        await vc.disconnect(force=True)
                    except Exception as e:
                        logging.error(f"Error disconnecting from guild {vc.guild.id}: {e}") # type: ignore
            
            valkey.delete(f"node:{self.node_id}:heartbeat")
        
        except Exception as e:
            logging.error(f"Error during graceful shutdown: {e}")
        
        await super().close()

