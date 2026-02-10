from dataclasses import dataclass
from enum import Enum, auto
import json
import logging
import time
from typing import Dict, Optional
import discord
from valkey import Valkey

from generated import brain_pb2, brain_pb2_grpc

class VoiceTransitionType(Enum):
    CONNECT = auto()
    DISCONNECT = auto()

@dataclass
class VoiceTransition:
    type: VoiceTransitionType
    target_channel_id: int | None
    session_id: str | None
    timestamp: float
    expiration: float

class StateService:
    def __init__(self, valkey: Valkey, brain_stub: brain_pb2_grpc.BrainStub, node_id: str) -> None:
        self.valkey = valkey
        self.brain = brain_stub
        self.node_id = node_id

        self._active_transitions: Dict[int, VoiceTransition] = {}
        self._session_ids: Dict[int, str] = {}

    def _get_node_heartbeat_key(self):
        return f"node:{self.node_id}:heartbeat"
    
    def _get_guild_connection_key(self, guild_id: int):
        return f"guild:{guild_id}:connection"
    
    def _get_guild_channel_key(self, guild_id: int):
        return f"guild:{guild_id}:channel"
    
    def get_session_id(self, guild_id: int) -> str | None:
        return self._session_ids.get(guild_id)
    
    def set_session_id(self, guild_id: int, session_id: str):
        self._session_ids[guild_id] = session_id
        logging.info(f"Stored session ID {session_id} for guild {guild_id}")
    
    def clear_session_id(self, guild_id: int):
        if guild_id in self._session_ids:
            del self._session_ids[guild_id]
            logging.info(f"Cleared session ID for guild {guild_id}")
    
    def report_heartbeat(self, ip_address: str, load: int):
        data = {"ip": ip_address, "load": load}
        self.valkey.set(self._get_node_heartbeat_key(), json.dumps(data), ex=5)

    def remove_node_heartbeat(self):
        return self.valkey.delete(self._get_node_heartbeat_key())

    def get_registered_node(self, guild_id: int):
        return self.valkey.get(self._get_guild_connection_key(guild_id))
    
    def get_registered_channel(self, guild_id: int):
        return self.valkey.get(self._get_guild_channel_key(guild_id))
    
    async def notify_state_change(self, guild: discord.Guild, channel: discord.VoiceChannel | None, reason):
        try:
            session_id = self.get_session_id(guild.id)
            req = brain_pb2.VoiceStateNotification(
                guild=brain_pb2.GuildContext(id=guild.id, name=guild.name),
                node_id=self.node_id,
                reason=reason
            )
            if session_id:
                req.session_id = session_id
            if channel:
                req.channel.CopyFrom(brain_pb2.ChannelContext(id=channel.id, name=channel.name))
            
            await self.brain.NotifyVoiceState(req) # type: ignore (BrainAsyncStub)
        except Exception as e:
            logging.error(f"Failed to notify Brain of state change: {e}")

    def register_intent(self, guild_id: int, transition_type: VoiceTransitionType, target_channel_id: int | None = None, session_id: str | None = None, ttl: float = 10.0):
        now = time.time()
        self._active_transitions[guild_id] = VoiceTransition(
            type=transition_type,
            target_channel_id=target_channel_id,
            session_id=session_id,
            timestamp=now,
            expiration=now + ttl
        )

    def get_intent(self, guild_id: int):
        intent = self._active_transitions.get(guild_id)
        if intent:
            if time.time() > intent.expiration:
                del self._active_transitions[guild_id]
                return None
            return intent
        return None
    
    def consume_intent(self, guild_id: int):
        if guild_id in self._active_transitions:
            del self._active_transitions[guild_id]
