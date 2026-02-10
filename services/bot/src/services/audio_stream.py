from generated import brain_pb2
from services.stream import BaseStreamService

class AudioStreamService(BaseStreamService[brain_pb2.UserAudioFrame]):
    def push_audio(self, guild_id: int, user_id: int, pcm_data: bytes, speech_prob: float, session_id: str | None = None):        
        frame = brain_pb2.UserAudioFrame(
            guild_id=guild_id,
            user_id=user_id,
            pcm_data=pcm_data,
            timestamp=self._get_time(),
            speech_probability=speech_prob
        )
        if session_id:
            frame.session_id = session_id
        
        self._enqueue(frame)

    async def _create_stream_call(self, generator, metadata):
        return self.brain.StreamAudio(generator, metadata=metadata)

    async def _process_stream(self, stream_call):
        async for response in stream_call:
            pass
