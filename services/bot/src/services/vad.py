import copy
import logging
from typing import Optional
import numpy as np
import torch
import torchaudio
from silero_vad import load_silero_vad, VADIterator

VAD_SAMPLE_RATE = 16000
VAD_WINDOW_SIZE = 512 # At 16k

class VADState:
    def __init__(self, model):
        self.model = model
        self.buffer = torch.tensor([], dtype=torch.float32)
        self.last_speech_prob = 0.0

class VADService:
    def __init__(self):
        self._source_model = None
        self.src_rate = 48000
        self.vad_rate = 16000
        self._resampler: Optional[torchaudio.transforms.Resample] = None

    def load_model(self):
        if self._source_model:
            return
        
        logging.info("Loading Silero VAD model...")
        try:
            self._model = load_silero_vad(onnx=False)
            self._resampler = torchaudio.transforms.Resample(self.src_rate, self.vad_rate)
            logging.info("Silero VAD model loaded successfully")
        except Exception as e:
            logging.error(f"Failed to load Silero VAD: {e}")
            raise

    def get_state(self) -> VADState:
        model_copy = copy.deepcopy(self._model)
        return VADState(model_copy)

    def process_audio(self, pcm_bytes: bytes, state: VADState) -> float:
        # To int16
        audio_np = np.frombuffer(pcm_bytes, dtype=np.int16)
        
        # To float32 tensor [-1, 1]
        audio_tensor = torch.from_numpy(audio_np.copy()).float() / 32768.0
        audio_tensor = audio_tensor.view(-1, 2).t() # (2, N)

        # Stereo to mono
        audio_mono = torch.mean(audio_tensor, dim=0, keepdim=True)

        # Resample 48k -> 16k
        audio_16k = self._resampler(audio_mono).squeeze() # type: ignore (Pylance doesn't understand logic)

        # Feed audio to VAD in 512-byte chunks as it expects
        state.buffer = torch.cat((state.buffer, audio_16k), dim=0)
        
        max_prob = -1.0
        processed_any = False

        while state.buffer.shape[0] >= VAD_WINDOW_SIZE:
            processed_any = True

            chunk = state.buffer[:VAD_WINDOW_SIZE]
            state.buffer = state.buffer[VAD_WINDOW_SIZE:]

            try:
                prob = state.model(chunk, VAD_SAMPLE_RATE).item()

                if prob > max_prob:
                    max_prob = prob
            except Exception as e:
                pass

        if processed_any:
            state.last_speech_prob = max_prob
            return max_prob
        else:
            return state.last_speech_prob
