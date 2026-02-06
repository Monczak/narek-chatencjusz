import uuid
import os
import logging

from containers import Container

logging.basicConfig(level=logging.INFO)

def main():
    with open(".version", "r") as version_file:
        version = version_file.read().strip()
    
    node_id = os.getenv("NODE_ID", f"bot-{uuid.uuid4().hex[:8]}")

    logging.info(f"Starting Narek Chatencjusz bot service - version {version}")
    logging.info(f"Node ID: {node_id}")

    container = Container()

    container.node_id.override(node_id)
    container.version.override(version)

    logging.info("Initializing resources...")
    container.init_resources()

    try:
        bot = container.bot()
        bot.setup_cogs()

        token = container.config.discord_bot_token()
        bot.run(token)
    finally:
        logging.info("Shutting down resources...")
        container.shutdown_resources()
        logging.info("Bye!")

if __name__ == "__main__":
    main()
