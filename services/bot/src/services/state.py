import json
from typing import Optional
from valkey import Valkey

class StateService:
    def __init__(self, valkey: Valkey, node_id: str) -> None:
        self.valkey = valkey
        self.node_id = node_id

    def _get_node_heartbeat_key(self):
        return f"node:{self.node_id}:heartbeat"
    
    def _get_guild_connection_key(self, guild_id: str):
        return f"guild:{guild_id}:connection"
    
    def _get_guild_channel_key(self, guild_id: str):
        return f"guild:{guild_id}:channel"
    
    def report_heartbeat(self, ip_address: str, load: int):
        data = {"ip": ip_address, "load": load}
        self.valkey.set(self._get_node_heartbeat_key(), json.dumps(data), ex=5)

    def remove_node_heartbeat(self):
        return self.valkey.delete(self._get_node_heartbeat_key())

    def get_registered_node(self, guild_id: str):
        return self.valkey.get(self._get_guild_connection_key(guild_id))
    
    def get_registered_channel(self, guild_id: str):
        return self.valkey.get(self._get_guild_channel_key(guild_id))
    
    def register_guild_session(self, guild_id: str):
        self.valkey.set(self._get_guild_connection_key(guild_id), self.node_id)

    def register_guild_channel(self, guild_id: str, channel_id: str):
        self.valkey.set(self._get_guild_channel_key(guild_id), channel_id)

    def update_guild_session(self, guild_id: str, channel_id: str):
        self.register_guild_session(guild_id)
        self.register_guild_channel(guild_id, channel_id)

    def clear_guild_session(self, guild_id: str) -> None:
        self.valkey.delete(self._get_guild_connection_key(guild_id))
        self.valkey.delete(self._get_guild_channel_key(guild_id))
