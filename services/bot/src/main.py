import asyncio
import uuid
import os
import logging

import discord.opus

from containers import Container
from bot import NarekChatencjuszBot

logging.basicConfig(level=logging.INFO)

# Monkey-patch advertised encryption modes - prevents a regression 
# causing Opus decode errors with audio from other users
# discord.voice_client.VoiceClient.supported_modes = ["xsalsa20_poly1305"] # type: ignore

async def run_bot():
    with open(".version", "r") as version_file:
        version = version_file.read().strip()
    
    node_id = os.getenv("NODE_ID", f"bot-{uuid.uuid4().hex[:8]}")

    logging.info(f"Starting Narek Chatencjusz bot service - version {version}")
    logging.info(f"Node ID: {node_id}")

    discord.opus._load_default()

    container = Container()

    container.node_id.override(node_id)
    container.version.override(version)

    logging.info("Initializing resources...")
    if asyncio.iscoroutinefunction(container.init_resources):
        await container.init_resources()
    else:
        container.init_resources()

    try:
        # Get services that need circular dependency injection
        bot: NarekChatencjuszBot = await container.bot() # type: ignore
        keepalive_service = await container.keepalive_service() # type: ignore
        audio_stream_service = await container.audio_stream_service() # type: ignore
        voice_service = await container.voice_service() # type: ignore
        
        keepalive_service.bot = bot
        
        audio_stream_service.voice = voice_service
        
        token = container.config.discord_bot_token()

        await bot.setup_cogs()
        
        async with bot:
            await bot.start(token)
    except KeyboardInterrupt:
        pass
    finally:
        logging.info("Shutting down resources...")
        shutdown = container.shutdown_resources()
        if asyncio.iscoroutine(shutdown):
            await shutdown
        logging.info("Bye!")

def main():
    try:
        asyncio.run(run_bot())
    except KeyboardInterrupt:
        pass

if __name__ == "__main__":
    main()
