import asyncio
import logging
from valkey import Valkey

from generated import brain_pb2
from services.bot_config import BotConfigService
from services.voice import VoiceService

BROADCAST_CHANNEL = "node:broadcast:commands"

class CommandListener:
    def __init__(
        self, 
        valkey_client: Valkey, 
        node_id: str,
        voice_service: VoiceService,
        bot_config_service: BotConfigService,
    ):
        self.valkey = valkey_client
        self.node_id = node_id
        self.voice = voice_service
        self.bot_config = bot_config_service
        self.pubsub = self.valkey.pubsub()
        self._running = False

    async def start(self):
        node_channel = f"node:{self.node_id}:commands"

        self.pubsub.subscribe(node_channel, BROADCAST_CHANNEL)
        logging.info("Listening for commands on %s and %s", node_channel, BROADCAST_CHANNEL)

        self._running = True
        while self._running:
            try:
                message = self.pubsub.get_message(ignore_subscribe_messages=True)
                if message and message["type"] in ("message", b"message"):
                    await self._handle_raw_message(message["data"])
                
                await asyncio.sleep(0.01)
            except Exception as e:
                logging.error(f"Error in command listener: {e}")
                await asyncio.sleep(1)
    
    async def _handle_raw_message(self, data: bytes):
        try:
            cmd = brain_pb2.BrainCommand()
            cmd.ParseFromString(data)

            msg_type = cmd.WhichOneof("command")

            match msg_type:
                case "connect":
                    await self.voice.execute_connect(
                        guild_ctx=cmd.connect.guild,
                        channel_ctx=cmd.connect.channel,
                        correlation_id=cmd.connect.correlation_id,
                        session_id=cmd.connect.session_id
                    )
                case "disconnect":
                    await self.voice.execute_disconnect(
                        cmd.disconnect.guild,
                        cmd.disconnect.correlation_id,
                        session_id=cmd.disconnect.session_id
                    )
                case "refresh_bot_config":
                    logging.info("Received RefreshBotConfig - refreshing bot settings")
                    await self.bot_config.refresh()
                case "error":
                    pass
        except Exception as e:
            logging.error(f"Failed to process command: {e}")
