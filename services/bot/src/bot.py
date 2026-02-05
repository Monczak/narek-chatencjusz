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
        self.add_cog(self.container.state_cog(bot=self, node_id=self.node_id))
        self.add_cog(self.container.voice_cog(bot=self, node_id=self.node_id))

    async def on_ready(self):
        logging.info(f"Logged in as {self.user}")
        logging.info(f"Narek Chatencjusz bot service is up and running")
