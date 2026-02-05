import uuid
import discord
import os
import logging

from containers import Container

from cogs.util import UtilCog
from cogs.state_manager import StateManager

logging.basicConfig(level=logging.INFO)

with open(".version", "r") as version_file:
    VERSION = version_file.read().strip()

bot = discord.Bot()

@bot.event
async def on_ready():
    logging.info(f"Logged in as {bot.user}")
    logging.info(f"Narek Chatencjusz bot service is up and running")

def main():
    NODE_ID = os.getenv("NODE_ID", f"bot-{uuid.uuid4().hex[:8]}")

    logging.info(f"Starting Narek Chatencjusz bot service - version {VERSION}")

    container = Container()
    logging.info("Initializing resources...")
    container.init_resources()

    util_cog = UtilCog(bot, container.util_service())
    bot.add_cog(util_cog)

    valkey_client = container.valkey_client()
    state_cog = StateManager(bot, valkey_client, NODE_ID)
    bot.add_cog(state_cog)

    try:
        token = container.config.discord_bot_token()
        bot.run(token)
    finally:
        logging.info("Shutting down resources...")
        container.shutdown_resources()
    
    logging.info("Bye!")

if __name__ == "__main__":
    main()
