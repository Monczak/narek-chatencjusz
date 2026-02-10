import asyncio
from typing import Dict
import discord
from discord.sinks import Sink, Filters

from services.audio_stream import AudioStreamService
from services.vad import VADService, VADState
from services.state import StateService

CHUNK_SIZE = 3840 # 20 ms of stereo audio at 48kHz

class GrpcVadAudioSink(Sink):
    def __init__(self, guild: discord.Guild, audio_service: AudioStreamService, vad_service: VADService, state_service: StateService):
        super().__init__()
        self.audio = audio_service
        self.vad = vad_service
        self.state = state_service
        self.guild = guild

        self._user_vad_states: Dict[int, VADState] = {}

    @Filters.container
    def write(self, data: bytes, user: int):
        # Dirty fix: Pycord keeps silence in the data to write to the sink if there is no voice activity
        # so we just take the last 20 ms of audio
        data = data[-CHUNK_SIZE:]

        if user not in self._user_vad_states:
            self._user_vad_states[user] = self.vad.get_state()

        state = self._user_vad_states[user]
        speech_prob = self.vad.process_audio(data, state)
        
        session_id = self.state.get_session_id(self.guild.id)
        
        self.audio.push_audio(self.guild.id, user, data, speech_prob, session_id)

    def cleanup(self):
        self._user_vad_states.clear()
        return super().cleanup()
