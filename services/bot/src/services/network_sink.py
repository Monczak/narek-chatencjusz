import array
import threading
import time
import discord
from discord.sinks import Sink

from services.audio_stream import AudioStreamService
from services.state import StateService

CHUNK_SIZE = 3840  # 20 ms of stereo audio at 48kHz
SILENCE_FRAME = bytes(CHUNK_SIZE)

def apply_smoothing_fade(prev_pcm: bytes, current_pcm: bytes, fade_samples: int) -> bytes:
    if not prev_pcm or len(prev_pcm) < 4 or len(current_pcm) < fade_samples * 4:
        return current_pcm
    
    prev_arr = array.array('h', prev_pcm)
    curr_arr = array.array('h', current_pcm)
    
    delta_l = prev_arr[-2] - curr_arr[0]
    delta_r = prev_arr[-1] - curr_arr[1]
    
    for i in range(fade_samples):
        t = i / fade_samples
        
        decay = (1.0 - t) ** 3 
        
        new_l = int(curr_arr[i*2] + (delta_l * decay))
        new_r = int(curr_arr[i*2+1] + (delta_r * decay))
        
        curr_arr[i*2] = max(-32768, min(32767, new_l))
        curr_arr[i*2+1] = max(-32768, min(32767, new_r))
        
    return curr_arr.tobytes()

class GrpcVadAudioSink(Sink):    
    def __init__(
        self, 
        guild: discord.Guild, 
        audio_service: AudioStreamService, 
        state_service: StateService,
        fade_ms: float = 10.0
    ):
        super().__init__()
        self.audio = audio_service
        self.state = state_service
        self.guild = guild
        
        self.fade_ms = max(1.0, min(fade_ms, 5.0))
        self.fade_samples = int((self.fade_ms / 1000.0) * 48000)
        
        self.user_buffers = {}
        self.buffer_lock = threading.Lock()
        
        self._is_running = True
        self._jitter_thread = threading.Thread(
            target=self._high_precision_jitter_loop, 
            name=f"jitter-{guild.id}", 
            daemon=True
        )
        self._jitter_thread.start()

    def write(self, data: bytes, user: int):
        frame = data[-CHUNK_SIZE:]
        with self.buffer_lock:
            if user not in self.user_buffers:
                self.user_buffers[user] = {
                    "data": bytearray(), 
                    "playing": False,
                    "last_frame": SILENCE_FRAME,
                    "last_played_pcm": SILENCE_FRAME,
                    "plc_count": 0,
                    "needs_smoothing": False
                }
            
            self.user_buffers[user]["data"].extend(frame)

    def _high_precision_jitter_loop(self):
        FRAME_DURATION = 0.02
        next_tick = time.perf_counter() + FRAME_DURATION
        
        while self._is_running:
            now = time.perf_counter()
            
            if now < next_tick:
                sleep_time = next_tick - now
                if sleep_time > 0.002:
                    time.sleep(sleep_time - 0.002) 
                continue 
                
            next_tick += FRAME_DURATION
            
            session_id = self.state.get_session_id(self.guild.id)
            if not session_id:
                continue
                
            with self.buffer_lock:
                for user, buf in list(self.user_buffers.items()):
                    
                    if not buf["playing"] and len(buf["data"]) >= CHUNK_SIZE * 4:
                        buf["playing"] = True
                        
                    if buf["playing"]:
                        if len(buf["data"]) >= CHUNK_SIZE:
                            # --- Normal playback ---
                            pcm = bytes(buf["data"][:CHUNK_SIZE])
                            del buf["data"][:CHUNK_SIZE]
                            
                            buf["last_frame"] = pcm
                            buf["plc_count"] = 0
                            
                            if buf["needs_smoothing"]:
                                pcm = apply_smoothing_fade(buf["last_played_pcm"], pcm, self.fade_samples)
                                buf["needs_smoothing"] = False
                                
                            buf["last_played_pcm"] = pcm
                            self.audio.push_audio(session_id, user, pcm)
                            
                        else:
                            # --- Buffer underflow - do packet loss concealment ---
                            buf["plc_count"] += 1
                            
                            if buf["plc_count"] <= 2:
                                plc_pcm = apply_smoothing_fade(buf["last_played_pcm"], buf["last_frame"], self.fade_samples)
                                
                                buf["needs_smoothing"] = True
                                buf["last_played_pcm"] = plc_pcm
                                self.audio.push_audio(session_id, user, plc_pcm)
                            else:
                                buf["playing"] = False
                                buf["plc_count"] = 0
                                buf["needs_smoothing"] = False
                                buf["last_played_pcm"] = SILENCE_FRAME

    def cleanup(self):
        self._is_running = False
        return super().cleanup()
