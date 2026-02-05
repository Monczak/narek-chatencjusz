import logging
import socket
import discord
from discord.ext import commands, tasks

from valkey import Valkey

class StateManager(commands.Cog):
    def __init__(self, bot: discord.Bot, valkey_client: Valkey, node_id: str):
        self.bot = bot
        self.valkey = valkey_client
        self.node_id = node_id

        self.ip_address = socket.gethostbyname(socket.gethostname())

        self.heartbeat.start()

    def _get_node_key(self):
        return f"nodes:{self.node_id}:heartbeat"

    def cog_unload(self):
        self.heartbeat.cancel()
        self.valkey.delete(self._get_node_key())

    @tasks.loop(seconds=3.0)
    async def heartbeat(self):
        data = {"ip": self.ip_address, "load": len(self.bot.voice_clients)}
        self.valkey.set(self._get_node_key(), str(data), ex=5)
