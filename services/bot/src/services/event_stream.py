from typing import List
import discord
from generated import brain_pb2
from services.stream import BaseStreamService

class EventStreamService(BaseStreamService[brain_pb2.VoiceSessionEvent]):
    def push_user_state_update(self, guild: discord.Guild, user: discord.Member | discord.User, channel: discord.VoiceChannel, change_type):
        event = brain_pb2.VoiceSessionEvent(
            guild=brain_pb2.GuildContext(id=guild.id, name=guild.name),
            node_id=self.node_id,
            timestamp=self._get_time(),
            user_state=brain_pb2.UserVoiceStateUpdate(
                user=brain_pb2.UserContext(id=user.id, display_name=user.display_name),
                channel=brain_pb2.ChannelContext(id=channel.id, name=channel.name),
                change_type=change_type
            )
        )
        self._enqueue(event)

    def push_session_state_update(self, guild: discord.Guild, change_type, channel: discord.VoiceChannel | None):
        update = brain_pb2.SessionUpdate(
            change_type=change_type
        )
        if channel:
            update.channel.CopyFrom(brain_pb2.ChannelContext(id=channel.id, name=channel.name))

        event = brain_pb2.VoiceSessionEvent(
            guild=brain_pb2.GuildContext(id=guild.id, name=guild.name),
            node_id=self.node_id,
            timestamp=self._get_time(),
            session_update=update
        )
        self._enqueue(event)

    def push_channel_snapshot(self, guild: discord.Guild, channel: discord.VoiceChannel, members: List[discord.Member]):
        snapshot_users = [
            brain_pb2.UserContext(id=m.id, display_name=m.display_name)
            for m in members
        ]

        event = brain_pb2.VoiceSessionEvent(
            guild=brain_pb2.GuildContext(id=guild.id, name=guild.name),
            node_id=self.node_id,
            timestamp=self._get_time(),
            channel_state_snapshot=brain_pb2.ChannelStateSnapshot(
                channel=brain_pb2.ChannelContext(id=channel.id, name=channel.name),
                users=snapshot_users
            )
        )
        self._enqueue(event)

    async def _create_stream_call(self, generator, metadata):
        return self.brain.StreamVoiceSessionEvents(generator, metadata=metadata)

    async def _process_stream(self, stream_call):
        await stream_call
            
