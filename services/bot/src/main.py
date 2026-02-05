import discord
import logging

from containers import Container

from cogs.util import UtilCog

logging.basicConfig(level=logging.INFO)

with open(".version", "r") as version_file:
    VERSION = version_file.read().strip()

bot = discord.Bot()

@bot.event
async def on_ready():
    logging.info(f"Logged in as {bot.user}")
    logging.info(f"Narek Chatencjusz bot service is up and running")

def main():
    logging.info(f"Starting Narek Chatencjusz bot service - version {VERSION}")

    container = Container()
    logging.info("Initializing resources...")
    container.init_resources()

    brain_service = container.brain_client()

    util_cog = UtilCog(bot, brain_service)
    bot.add_cog(util_cog)

    try:
        token = container.config.discord_bot_token()
        bot.run(token)
    finally:
        logging.info("Shutting down resources...")
        container.shutdown_resources()
    
    logging.info("Bye!")

if __name__ == "__main__":
    main()
