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
    
    node_id = os.getenv("NODE_ID", f"bot-{uuid.uuid4().hex[:8]}")

    logging.info(f"Starting Narek Chatencjusz bot service - version {VERSION}")
    logging.info(f"Node ID: {node_id}")

    container = Container()

    container.node_id.override(node_id)

    logging.info("Initializing resources...")
    container.init_resources()

    container.wire(modules=[__name__])

    try:
        bot = NarekChatencjuszBot(
            container=container,
            node_id=node_id,
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
