import logging
from typing import Optional, Tuple
from generated import brain_pb2, brain_pb2_grpc

class VoiceService:
    def __init__(self, brain: brain_pb2_grpc.BrainStub) -> None:
        self.brain = brain

    def request_join(self, guild_id: str, channel_id: str, node_id: str) -> Tuple[bool, Optional[int], str]:
        try:
            req = brain_pb2.JoinChannelRequest(
                guild_id=guild_id,
                channel_id=channel_id,
                node_id=node_id
            )
            res = self.brain.JoinChannel(req)
            return res.success, res.instruction, res.message
        except Exception as e:
            logging.error(f"Brain voice join error: {e}")
            raise
    
    def notify_leave(self, guild_id: str, node_id: str) -> None:
        try:
            req = brain_pb2.LeaveChannelRequest(guild_id=guild_id, node_id=node_id)
            self.brain.LeaveChannel(req)
        except Exception as e:
            logging.error(f"Brain voice leave error: {e}")
