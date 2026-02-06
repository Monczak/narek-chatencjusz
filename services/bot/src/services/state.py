import json
import logging
from typing import Optional
from valkey import Valkey

from generated import brain_pb2, brain_pb2_grpc

class StateService:
    def __init__(self, valkey: Valkey, brain_stub: brain_pb2_grpc.BrainStub, node_id: str) -> None:
        self.valkey = valkey
        self.brain = brain_stub
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

    
    def notify_state_change(self, guild_id: str, channel_id: str | None, reason):
        try:
            req = brain_pb2.VoiceStateNotification(
                guild_id=guild_id,
                node_id=self.node_id,
                reason=reason
            )
            if channel_id:
                req.channel_id = channel_id
            
            self.brain.NotifyVoiceState(req)
        except Exception as e:
            logging.error(f"Failed to notify Brain of state change: {e}")
