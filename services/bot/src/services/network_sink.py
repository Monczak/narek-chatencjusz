import discord
from discord.sinks import Sink, Filters

from services.audio_stream import AudioStreamService
from services.state import StateService

CHUNK_SIZE = 3840  # 20 ms of stereo audio at 48kHz

class GrpcVadAudioSink(Sink):    
    def __init__(self, guild: discord.Guild, audio_service: AudioStreamService, state_service: StateService):
        super().__init__()
        self.audio = audio_service
        self.state = state_service
        self.guild = guild

    @Filters.container
    def write(self, data: bytes, user: int):
        # Dirty fix: Pycord keeps silence in the data to write to the sink if there is no voice activity
        # so we just take the last 20 ms of audio
        data = data[-CHUNK_SIZE:]
        
        session_id = self.state.get_session_id(self.guild.id)
        self.audio.push_audio(self.guild.id, user, data, session_id)

    def cleanup(self):
        return super().cleanup()
