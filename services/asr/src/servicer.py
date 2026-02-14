import asyncio
from concurrent.futures import ThreadPoolExecutor
import logging

from faster_whisper import WhisperModel
import numpy as np
from generated import asr_pb2, asr_pb2_grpc
from config import Settings

logger = logging.getLogger(__name__)

SAMPLE_RATE = 16000

class AsrServicer(asr_pb2_grpc.AsrServicer):
    def __init__(self, settings: Settings) -> None:
        logger.info(f"Loading faster-whisper model \"{settings.model}\" on {settings.device} (compute type {settings.compute_type}, flashattn={settings.flash_attention})")

        self._model = WhisperModel(
            settings.model,
            device=settings.device,
            compute_type=settings.compute_type,
            num_workers=settings.num_workers
        )

        self._beam_size = settings.beam_size
        self._min_samples = int(settings.min_utterance_seconds * SAMPLE_RATE)

        self._executor = ThreadPoolExecutor(
            max_workers=settings.num_workers,
            thread_name_prefix="whisper"
        )

        logger.info("Loaded faster-whisper model")
    
    async def Transcribe(self, request: asr_pb2.UtteranceRequest, context) -> asr_pb2.TranscriptResponse:
        empty = asr_pb2.TranscriptResponse(
            session_id=request.session_id,
            user_id=request.user_id,
            text="",
            started_at_ms=request.started_at_ms,
            ended_at_ms=request.ended_at_ms,
        )

        audio = np.frombuffer(request.pcm_f32_mono_16k, dtype=np.float32)

        if len(audio) < self._min_samples:
            logger.debug(f"Utterance too short ({len(audio)} samples) - skipping")
            return empty
        
        loop = asyncio.get_running_loop()
        try:
            result = await loop.run_in_executor(
                self._executor,
                self._transcribe_sync,
                audio,
                request.session_id,
                request.user_id,
            )
        except Exception:
            logger.exception(f"Transcription failed for session {request.session_id} user {request.user_id}")
            return empty
        
        return asr_pb2.TranscriptResponse(
            session_id=request.session_id,
            user_id=request.user_id,
            text=result["text"],
            confidence=result["confidence"],
            language=result["language"],
            started_at_ms=request.started_at_ms,
            ended_at_ms=request.ended_at_ms,
        )

    def _transcribe_sync(self, audio: np.ndarray, session_id: str, user_id: int) -> dict:
        segments_gen, info = self._model.transcribe(audio, beam_size=self._beam_size, vad_filter=False)
        text = "".join(seg.text for seg in segments_gen).strip()

        logger.info(
            f"ASR: session={session_id} user={user_id} lang={info.language}({info.language_probability:.2f}) text={text}"
        )
        return {
            "text": text,
            "language": info.language,
            "confidence": info.language_probability
        }