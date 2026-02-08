import asyncio
from typing import Awaitable, Callable
import discord
import logging
import time
from generated import brain_pb2_grpc, brain_pb2

class EventStreamService:
    def __init__(self, brain_stub_factory: Callable[[], Awaitable[brain_pb2_grpc.BrainStub]], node_id: str):
        self.brain_factory = brain_stub_factory
        self.brain: brain_pb2_grpc.BrainStub = None # type: ignore
        self.node_id = node_id

        self._queue = asyncio.Queue()
        self._stop_event = asyncio.Event()
        self._task: asyncio.Task | None = None

    async def start(self):
        if self.brain is None:
            self.brain = await self.brain_factory()

        if self._task is None:
            self._task = asyncio.create_task(self._stream_loop())

    async def stop(self):
        self._stop_event.set()
        if self._task:
            self._task.cancel()
            try:
                await self._task
            except asyncio.CancelledError:
                pass
            self._task = None

    def _get_time(self):
        return int(time.time() * 1000)

    def push_user_state_update(self, guild_id: str, user: discord.Member | discord.User, channel_id: str, change_type):
        event = brain_pb2.VoiceSessionEvent(
            guild_id=guild_id,
            node_id=self.node_id,
            timestamp=self._get_time(),
            user_state=brain_pb2.UserVoiceStateUpdate(
                user_id=str(user.id),
                user_display_name=user.display_name,
                channel_id=channel_id,
                change_type=change_type
            )
        )
        self._queue.put_nowait(event)

    def push_session_state_update(self, guild_id: str, change_type):
        event = brain_pb2.VoiceSessionEvent(
            guild_id=guild_id,
            node_id=self.node_id,
            timestamp=self._get_time(),
            session_update=brain_pb2.SessionUpdate(
                change_type=change_type
            )
        )
        self._queue.put_nowait(event)

    async def _event_generator(self):
        while not self._stop_event.is_set():
            try:
                event = await self._queue.get()
                yield event
            except asyncio.CancelledError:
                break

    async def _stream_loop(self):
        while not self._stop_event.is_set():
            gen = self._event_generator()
            stream_call = None
            should_backoff = False
            
            try:
                logging.info("Opening voice event stream to Brain...")
                if self.brain:
                    metadata = (("node_id", self.node_id), )
                    stream_call = self.brain.StreamVoiceSessionEvents(
                        gen, # type: ignore (BrainAsyncStub)
                        metadata=metadata
                    )
                    logging.info("Voice event stream opened")
                    await stream_call # type: ignore (BrainAsyncStub)
            except asyncio.CancelledError:
                break
            except Exception as e:
                logging.warning(f"Voice event stream disconnected: {e}. Retrying in 5 seconds...")
                should_backoff = True
            finally:
                if stream_call:
                    stream_call.cancel() # type: ignore (BrainAsyncStub)

                try:
                    await gen.aclose()
                except RuntimeError:
                    pass
                
                # Clean up zombie consumers
                old_queue = self._queue
                self._queue = asyncio.Queue()

                while not old_queue.empty():
                    try:
                        self._queue.put_nowait(old_queue.get_nowait())
                    except asyncio.QueueEmpty:
                        break

                await asyncio.sleep(0.1)

            if should_backoff and not self._stop_event.is_set():
                await asyncio.sleep(5)
            
