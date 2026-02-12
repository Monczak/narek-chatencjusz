import asyncio
import logging
import time
from typing import Dict, Optional
from generated import brain_pb2, brain_pb2_grpc

class SessionAudioStream:
    def __init__(self, session_id: str, guild_id: int, brain_stub: brain_pb2_grpc.BrainStub, voice_service):
        self.session_id = session_id
        self.guild_id = guild_id
        self.brain = brain_stub
        self.voice = voice_service
        
        self._input_queue = asyncio.Queue(maxsize=100)
        self._stop_event = asyncio.Event()
        self._stream_task: Optional[asyncio.Task] = None
        
        self._output_queue = asyncio.Queue(maxsize=50) # Max 1 s buffer to prevent runaway latency
        self._playback_task: Optional[asyncio.Task] = None
        
        logging.info(f"Created SessionAudioStream for session {session_id}, guild {guild_id}")
    
    async def start(self):
        if self._stream_task is None:
            self._stream_task = asyncio.create_task(self._run())
            self._playback_task = asyncio.create_task(self._playback_loop())
    
    async def stop(self):
        logging.info(f"Stopping audio stream for session {self.session_id}")
        self._stop_event.set()
        
        if self._playback_task:
            self._playback_task.cancel()
            
        if self._stream_task:
            try:
                await asyncio.wait_for(self._stream_task, timeout=5.0)
            except asyncio.TimeoutError:
                logging.warning(f"Audio stream for session {self.session_id} did not stop within timeout")
                self._stream_task.cancel()
            except Exception as e:
                logging.error(f"Error stopping audio stream for session {self.session_id}: {e}")
    
    def push_audio(self, user_id: int, pcm_data: bytes):
        frame = brain_pb2.UserAudioFrame(
            session_id=self.session_id,
            guild_id=self.guild_id,
            user_id=user_id,
            pcm_data=pcm_data,
            timestamp=int(time.time() * 1000)
        )
        
        try:
            self._input_queue.put_nowait(frame)
        except asyncio.QueueFull:
            logging.warning(f"Audio queue full for session {self.session_id} - dropping frame")
    
    async def _input_generator(self):
        while not self._stop_event.is_set():
            try:
                frame = await asyncio.wait_for(self._input_queue.get(), timeout=0.1)
                yield frame
            except asyncio.TimeoutError:
                continue
            except asyncio.CancelledError:
                break
                
    async def _playback_loop(self):
        FRAME_DURATION = 0.02
        SILENCE_FRAME = b'\x00' * 3840
        HIGH_WATER_FRAMES = 10   # ~200ms — above this we're accumulating latency
        TARGET_DEPTH_FRAMES = 2  # drain to ~40ms of buffer

        next_frame_time = time.perf_counter() + FRAME_DURATION

        long_dropout_time = time.time()
        long_dropout_mode = False
        prev_long_dropout_mode = False

        while not self._stop_event.is_set():
            try:
                # If the queue is deep, we've accumulated latency — drain it
                depth = self._output_queue.qsize()
                if depth > HIGH_WATER_FRAMES:
                    drained = 0
                    while self._output_queue.qsize() > TARGET_DEPTH_FRAMES:
                        try:
                            self._output_queue.get_nowait()
                            drained += 1
                        except asyncio.QueueEmpty:
                            break
                    logging.warning(
                        f"Output queue was {depth} frames deep — drained {drained} frames to reduce latency"
                    )

                try:
                    pcm_data = self._output_queue.get_nowait()
                    await self.voice.send_audio_to_guild(self.guild_id, pcm_data)
                    long_dropout_mode = False
                except asyncio.QueueEmpty:
                    await self.voice.send_audio_to_guild(self.guild_id, SILENCE_FRAME)
                    # logging.debug("Sending silence because queue was empty - long dropout?")
                    long_dropout_mode = True

                if long_dropout_mode and not prev_long_dropout_mode:
                    logging.warning(f"LONG DROPOUT STARTED - {time.time()} (delta {time.time() - long_dropout_time})")
                    long_dropout_time = time.time()
                elif not long_dropout_mode and prev_long_dropout_mode:
                    logging.warning(f"LONG DROPOUT STOPPED - {time.time()} (delta {time.time() - long_dropout_time})")
                    long_dropout_time = time.time()
                prev_long_dropout_mode = long_dropout_mode

                now = time.perf_counter()
                sleep_time = next_frame_time - now
                if sleep_time > 0:
                    await asyncio.sleep(sleep_time)

                next_frame_time += FRAME_DURATION

                if now > next_frame_time + 0.1:
                    logging.warning(f"Playback lag detected. Resetting clock.")
                    next_frame_time = now + FRAME_DURATION
                    while not self._output_queue.empty():
                        self._output_queue.get_nowait()

            except asyncio.CancelledError:
                break
            except Exception as e:
                logging.error(f"Error in playback loop: {e}")
                await asyncio.sleep(0.02)
    
    async def _run(self):
        while not self._stop_event.is_set():
            try:
                logging.info(f"Opening bidirectional audio stream for session {self.session_id}")
                metadata = (("session_id", self.session_id),)
                
                call = self.brain.StreamAudio(self._input_generator(), metadata=metadata) # type: ignore (BrainAsyncStub)
                
                async for audio_frame in call: # type: ignore (BrainAsyncStub)
                    if self._stop_event.is_set():
                        break
                    
                    # Push incoming frames into our elastic Jitter Buffer instead of sending immediately
                    try:
                        self._output_queue.put_nowait(audio_frame.pcm_data)
                    except asyncio.QueueFull:
                        # Prevent runaway latency by popping the oldest unplayed frame
                        try:
                            self._output_queue.get_nowait()
                            self._output_queue.put_nowait(audio_frame.pcm_data)
                        except asyncio.QueueEmpty:
                            pass
                
            except Exception as e:
                if not self._stop_event.is_set():
                    logging.warning(f"Audio stream error for session {self.session_id}: {e}")
                    await asyncio.sleep(5)
                else:
                    break
        
        logging.info(f"Audio stream ended for session {self.session_id}")


class AudioStreamService:
    def __init__(self, brain_stub_factory, node_id: str, voice_service):
        self.brain_stub_factory = brain_stub_factory
        self.node_id = node_id
        self.voice = voice_service
        self._sessions: Dict[str, SessionAudioStream] = {}
        self.brain_stub: Optional[brain_pb2_grpc.BrainStub] = None
    
    async def start(self):
        self.brain_stub = await self.brain_stub_factory()
        logging.info("AudioStreamService initialized")
    
    async def start_session(self, session_id: str, guild_id: int):
        if session_id in self._sessions:
            logging.warning(f"Audio stream for session {session_id} already exists")
            return
        
        if not self.brain_stub:
            logging.error("AudioStreamService not initialized - cannot start session")
            return
        
        stream = SessionAudioStream(session_id, guild_id, self.brain_stub, self.voice)
        await stream.start()
        self._sessions[session_id] = stream
        logging.info(f"Started audio stream for session {session_id}")
    
    async def stop_session(self, session_id: str):
        if session_id in self._sessions:
            await self._sessions[session_id].stop()
            del self._sessions[session_id]
            logging.info(f"Stopped audio stream for session {session_id}")
    
    def push_audio(self, session_id: str | None, user_id: int, pcm_data: bytes):
        if session_id and session_id in self._sessions:
            self._sessions[session_id].push_audio(user_id, pcm_data)
    
    async def stop(self):
        logging.info(f"Stopping all audio streams ({len(self._sessions)} active)")
        for session_id in list(self._sessions.keys()):
            await self.stop_session(session_id)
