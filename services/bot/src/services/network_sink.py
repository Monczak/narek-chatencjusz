import asyncio
from typing import Dict
from discord.sinks import Sink, Filters

from services.audio import AudioStreamService
from services.vad import VADService, VADState

CHUNK_SIZE = 3840 # 20 ms of stereo audio at 48kHz

class GrpcVadAudioSink(Sink):
    def __init__(self, audio_service: AudioStreamService, vad_service: VADService):
        super().__init__()
        self.audio = audio_service
        self.vad = vad_service

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
        self.audio.push_audio(user, data, speech_prob)

    def cleanup(self):
        self._user_vad_states.clear()
        return super().cleanup()
