import asyncio
import logging
import time
from typing import AsyncGenerator, Awaitable, Callable, Generic, TypeVar

from generated import brain_pb2_grpc

T = TypeVar("T")

class BaseStreamService(Generic[T]):
    def __init__(self, brain_stub_factory: Callable[[], Awaitable[brain_pb2_grpc.BrainStub]], node_id: str):
        self.brain_factory = brain_stub_factory
        self.brain: brain_pb2_grpc.BrainStub = None # type: ignore
        self.node_id = node_id

        self._queue = asyncio.Queue()
        self._stop_event = asyncio.Event()
        self._stream_task: asyncio.Task | None = None

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
    
    def _enqueue(self, item: T):
        try:
            self._queue.put_nowait(item)
        except asyncio.QueueFull:
            logging.warning(f"{self.__class__.__name__} queue full - dropping item")
    
    async def _generator(self) -> AsyncGenerator[T, None]:
        while not self._stop_event.is_set():
            try:
                item = await self._queue.get()
                yield item
            except asyncio.CancelledError:
                break

    async def _create_stream_call(self, generator, metadata):
        raise NotImplementedError
    
    async def _process_stream(self, stream_call):
        raise NotImplementedError
    
    async def _stream_loop(self):
        while not self._stop_event.is_set():
            gen = self._generator()
            stream_call = None
            should_backoff = False
            
            try:
                logging.info(f"Opening {self.__class__.__name__} stream to Brain...")
                if self.brain:
                    metadata = (("node_id", self.node_id), )
                    stream_call = await self._create_stream_call(gen, metadata)
                    logging.info("Audio stream opened")
                    
                    await self._process_stream(stream_call)

            except asyncio.CancelledError:
                break
            except Exception as e:
                logging.warning(f"{self.__class__.__name__} stream disconnected: {e}. Retrying in 5 seconds...")
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
