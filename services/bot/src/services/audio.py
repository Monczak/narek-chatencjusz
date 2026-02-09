import asyncio
import logging
import time
from typing import Awaitable, Callable, Optional

from generated import brain_pb2_grpc, brain_pb2

class AudioStreamService:
    def __init__(self, brain_stub_factory: Callable[[], Awaitable[brain_pb2_grpc.BrainStub]], node_id: str):
        self.brain_factory = brain_stub_factory
        self.brain: brain_pb2_grpc.BrainStub = None # type: ignore
        self.node_id = node_id

        self._send_queue = asyncio.Queue()
        self._stream_task: Optional[asyncio.Task] = None
        self._stop_event = asyncio.Event()

    async def start(self):
        if self.brain is None:
            self.brain = await self.brain_factory()

        if self._stream_task is None:
            self._stream_task = asyncio.create_task(self._stream_loop())

    async def stop(self):
        self._stop_event.set()
        if self._stream_task:
            self._stream_task.cancel()
            try:
                await self._stream_task
            except asyncio.CancelledError:
                pass
            self._stream_task = None

    def _get_time(self):
        return int(time.time() * 1000)

    def push_audio(self, user_id: int, pcm_data: bytes, speech_prob: float):        
        try:
            frame = brain_pb2.UserAudioFrame(
                user_id=user_id,
                pcm_data=pcm_data,
                timestamp=self._get_time(),
                speech_probability=speech_prob
            )
            self._send_queue.put_nowait(frame)
        except asyncio.QueueFull:
            logging.warning("Audio queue full - dropping frame")

    async def _frame_generator(self):
        while not self._stop_event.is_set():
            try:
                frame = await self._send_queue.get()
                yield frame
            except asyncio.CancelledError:
                break

    async def _stream_loop(self):
        while not self._stop_event.is_set():
            gen = self._frame_generator()
            stream_call = None
            should_backoff = False
            
            try:
                logging.info("Opening audio stream to Brain...")
                if self.brain:
                    metadata = (("node_id", self.node_id), )
                    stream_call = self.brain.StreamAudio(
                        gen, # type: ignore (BrainAsyncStub)
                        metadata=metadata
                    )
                    logging.info("Audio stream opened")
                    
                    async for response_frame in stream_call: # type: ignore (BrainAsyncStub)
                        # TODO: Handle incoming audio frames
                        pass

            except asyncio.CancelledError:
                break
            except Exception as e:
                logging.warning(f"Audio stream disconnected: {e}. Retrying in 5 seconds...")
                should_backoff = True
            finally:
                if stream_call:
                    stream_call.cancel() # type: ignore (BrainAsyncStub)

                try:
                    await gen.aclose()
                except RuntimeError:
                    pass
                
                # Clean up zombie consumers
                old_queue = self._send_queue
                self._send_queue = asyncio.Queue()

                while not old_queue.empty():
                    try:
                        self._send_queue.put_nowait(old_queue.get_nowait())
                    except asyncio.QueueEmpty:
                        break

                await asyncio.sleep(0.1)

            if should_backoff and not self._stop_event.is_set():
                await asyncio.sleep(5)
