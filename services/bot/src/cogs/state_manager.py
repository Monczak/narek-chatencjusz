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
        self.reconcile_state.start()

    def _get_node_key(self):
        return f"node:{self.node_id}:heartbeat"

    def cog_unload(self):
        self.heartbeat.cancel()
        self.reconcile_state.cancel()
        self.valkey.delete(self._get_node_key())

    @tasks.loop(seconds=3.0)
    async def heartbeat(self):
        data = {"ip": self.ip_address, "load": len(self.bot.voice_clients)}
        self.valkey.set(self._get_node_key(), str(data), ex=5)

    @tasks.loop(seconds=5.0)
    async def reconcile_state(self):
        connected_guilds = {vc.guild.id: vc for vc in self.bot.voice_clients if vc.is_connected()} # type: ignore

        for guild_id, vc in connected_guilds.items():
            key = f"guild:{guild_id}:connection"
            registered_node = self.valkey.get(key)

            if registered_node is None:
                logging.warning(f"Healing: I am in guild {guild_id} but Valkey didn't know -- registering session")
                self.valkey.set(key, self.node_id)
            elif registered_node != self.node_id:
                logging.warning(f"Conflict: I am in guild {guild_id} but Valkey thinks {registered_node} is there -- disconnecting")
                await vc.disconnect(force=True)
