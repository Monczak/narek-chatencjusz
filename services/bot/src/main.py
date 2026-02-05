import uuid
import discord
import os
import logging

from containers import Container
from bot import NarekChatencjuszBot

logging.basicConfig(level=logging.INFO)

def main():
    with open(".version", "r") as version_file:
        VERSION = version_file.read().strip()
    
    NODE_ID = os.getenv("NODE_ID", f"bot-{uuid.uuid4().hex[:8]}")

    logging.info(f"Starting Narek Chatencjusz bot service - version {VERSION}")
    logging.info(f"Node ID: {NODE_ID}")

    container = Container()

    logging.info("Initializing resources...")
    container.init_resources()

    try:
        bot = NarekChatencjuszBot(
            container=container,
            node_id=NODE_ID,
            version=VERSION
        )
        bot.setup_cogs()

        token = container.config.discord_bot_token()
        bot.run(token)
    finally:
        logging.info("Shutting down resources...")
        container.shutdown_resources()
        logging.info("Bye!")

if __name__ == "__main__":
    main()
