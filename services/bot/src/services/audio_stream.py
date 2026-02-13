"""
UDP audio transport.

Wire format
-----------
Inbound  (Bot → Brain):  type(1) + guild_id(8 LE) + user_id(8 LE) + session_uuid(16 BE) + pcm(3840) = 3873 B
Outbound (Brain → Bot):  type(1) + guild_id(8 LE) + pcm(3840) = 3849 B

All integers little-endian except the session UUID which is 16 raw big-endian bytes
(Python uuid.UUID.bytes, C# new Guid(span, bigEndian: true)).

Single-socket design
--------------------
We use one asyncio DatagramTransport for both directions:
  - asyncio owns it for receiving (registered with add_reader in the event loop)
  - push_audio() calls sendto() on the raw OS socket directly from pycord's recording thread

This is safe because:
  1. asyncio only reads the fd; our sendto() calls are write-only syscalls.
  2. UDP sendto() is atomic at the kernel level for datagrams of this size.
  3. Python's GIL serialises the Python-level call.

Critically, using one socket means the brain always sees the same source port and can
send replies back to the right place. The old two-socket design broke this: _send_sock
had one ephemeral port, _recv_transport had a different one, so replies went to
_send_sock but _recv_transport never saw them.
"""

import asyncio
import logging
import socket
import struct
import time
import uuid
from typing import Dict, Optional

BRAIN_UDP_PORT  = 5051
PCM_FRAME_SIZE  = 3840          # 48 kHz stereo 16-bit 20 ms
TYPE_AUDIO_IN   = 0x01
TYPE_AUDIO_OUT  = 0x02
OUTBOUND_HEADER = 9             # type(1) + guild_id(8)
SILENCE_FRAME   = b'\x00' * PCM_FRAME_SIZE


# ── Per-session stream ──────────────────────────────────────────────────────────


class SessionAudioStream:
    """
    Owns the UDP transport for one voice session.
    - push_audio()   : called by GrpcVadAudioSink with raw PCM captured from Discord
    - _playback_loop : reads received frames and drives VoiceService.send_audio_to_guild
    """

    def __init__(self, session_id: str, guild_id: int, brain_host: str, voice_service):
        self.session_id  = session_id
        self.guild_id    = guild_id
        self.brain_host  = brain_host
        self.voice       = voice_service

        # Precompute invariant packet header pieces
        self._session_bytes = uuid.UUID(session_id).bytes       # 16 bytes, big-endian
        self._guild_packed  = struct.pack('<Q', guild_id)       # 8 bytes, little-endian
        self._brain_addr    = (brain_host, BRAIN_UDP_PORT)

        self._output_queue: asyncio.Queue[bytes] = asyncio.Queue(maxsize=50)
        self._stop_event = asyncio.Event()

        # One asyncio transport owns the socket for receiving.
        # We extract its underlying OS socket and use it directly for sending,
        # so the brain always sees the same source port and can reply to it.
        self._transport: Optional[asyncio.DatagramTransport] = None
        self._raw_sock:  Optional[socket.socket]             = None
        self._playback_task: Optional[asyncio.Task]          = None

        logging.info("SessionAudioStream created for session %s guild %d", session_id, guild_id)

    async def start(self):
        # Create the socket ourselves so we hold a reference to the real socket.socket
        # before asyncio wraps it. get_extra_info('socket') returns a TransportSocket
        # proxy that lacks sendto(), so we must own the fd ourselves.
        # Passing it via sock= means asyncio uses the same fd for receiving, so the
        # brain always sees inbound packets arriving from the same source port it
        # should send replies to.
        self._raw_sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self._raw_sock.bind(('', 0))  # ephemeral port assigned by the OS

        loop = asyncio.get_running_loop()
        self._transport, _ = await loop.create_datagram_endpoint(
            lambda: _AudioProtocol(self._output_queue),
            sock=self._raw_sock,
        )
        self._playback_task = asyncio.create_task(
            self._playback_loop(), name=f"playback-{self.guild_id}")
        logging.info("UDP audio started for session %s on port %d",
                     self.session_id, self._raw_sock.getsockname()[1])

    async def stop(self):
        self._stop_event.set()
        if self._playback_task:
            self._playback_task.cancel()
            try:
                await self._playback_task
            except asyncio.CancelledError:
                pass
        if self._transport:
            self._transport.close()
        # _raw_sock is owned by the transport; closing the transport closes it.
        self._raw_sock = None
        logging.info("UDP audio stopped for session %s", self.session_id)

    def push_audio(self, user_id: int, pcm_data: bytes):
        """Hot path — called from pycord's recording thread. Must be thread-safe.

        socket.sendto() is a direct syscall; safe to call from any thread as long as
        we don't also write from the event loop (we don't — asyncio only reads this fd).
        """
        if self._raw_sock is None:
            return

        packet = (
            bytes([TYPE_AUDIO_IN])
            + self._guild_packed
            + struct.pack('<Q', user_id)
            + self._session_bytes
            + pcm_data
        )
        try:
            self._raw_sock.sendto(packet, self._brain_addr)
        except OSError:
            pass  # socket closed during shutdown

    # ── Playback loop ───────────────────────────────────────────────────────────

    async def _playback_loop(self):
        PRE_BUFFER   = 10    # frames to accumulate before starting (~80 ms)
        HIGH_WATER   = 15   # ~200 ms — drain if deeper than this
        TARGET_DEPTH = 6    # drain down to ~60 ms
        FRAME        = 0.02

        # Wait until the jitter buffer has a minimum depth before starting.
        # This absorbs the initial burst of network jitter and prevents the
        # first few seconds from crackling due to an empty queue.
        logging.info("Playback waiting for %d buffered frames...", PRE_BUFFER)
        while not self._stop_event.is_set():
            if self._output_queue.qsize() >= PRE_BUFFER:
                break
            await asyncio.sleep(0.005)
        logging.info("Jitter buffer primed, starting playback")

        next_frame_time = time.perf_counter() + FRAME

        while not self._stop_event.is_set():
            try:
                # Latency correction: drain if the queue has accumulated
                depth = self._output_queue.qsize()
                if depth > HIGH_WATER:
                    drained = 0
                    while self._output_queue.qsize() > TARGET_DEPTH:
                        try:
                            self._output_queue.get_nowait()
                            drained += 1
                        except asyncio.QueueEmpty:
                            break
                    logging.warning("Output queue depth %d → drained %d frames", depth, drained)

                try:
                    pcm = self._output_queue.get_nowait()
                except asyncio.QueueEmpty:
                    pcm = SILENCE_FRAME
                    logging.warning("LONG DROPOUT FUCK ME")

                await self.voice.send_audio_to_guild(self.guild_id, pcm)

                # Precise pacing
                now   = time.perf_counter()
                sleep = next_frame_time - now
                if sleep > 0:
                    await asyncio.sleep(sleep)

                next_frame_time += FRAME

                # If we've fallen more than 100 ms behind, reset the clock and
                # re-prime the jitter buffer before resuming playback.
                if time.perf_counter() > next_frame_time + 0.1:
                    logging.warning("Playback clock drift — resetting and re-priming jitter buffer")
                    while not self._output_queue.empty():
                        try:
                            self._output_queue.get_nowait()
                        except asyncio.QueueEmpty:
                            break
                    # Re-prime
                    while not self._stop_event.is_set():
                        if self._output_queue.qsize() >= PRE_BUFFER:
                            break
                        await asyncio.sleep(0.005)
                    next_frame_time = time.perf_counter() + FRAME

            except asyncio.CancelledError:
                break
            except Exception as exc:
                logging.error("Playback error: %s", exc)
                await asyncio.sleep(FRAME)


# ── asyncio DatagramProtocol ────────────────────────────────────────────────────


class _AudioProtocol(asyncio.DatagramProtocol):
    """Feeds received UDP packets straight into the output queue."""

    def __init__(self, queue: asyncio.Queue):
        self._queue = queue

    def datagram_received(self, data: bytes, addr):
        if len(data) < OUTBOUND_HEADER + PCM_FRAME_SIZE or data[0] != TYPE_AUDIO_OUT:
            return
        pcm = data[OUTBOUND_HEADER : OUTBOUND_HEADER + PCM_FRAME_SIZE]
        try:
            self._queue.put_nowait(pcm)
        except asyncio.QueueFull:
            # Drop oldest to make room
            try:
                self._queue.get_nowait()
                self._queue.put_nowait(pcm)
            except asyncio.QueueEmpty:
                pass

    def error_received(self, exc: Exception):
        logging.warning("UDP error: %s", exc)

    def connection_lost(self, exc):
        if exc:
            logging.warning("UDP connection lost: %s", exc)


# ── Service ──────────────────────────────────────────────────────────────────────


class AudioStreamService:
    def __init__(self, brain_host: str, node_id: str, voice_service):
        self.brain_host = brain_host
        self.node_id    = node_id
        self.voice      = voice_service
        self._sessions: Dict[str, SessionAudioStream] = {}

    async def start(self):
        logging.info("AudioStreamService (UDP) initialised, brain host: %s", self.brain_host)

    async def start_session(self, session_id: str, guild_id: int):
        if session_id in self._sessions:
            logging.warning("Audio session %s already exists", session_id)
            return

        stream = SessionAudioStream(session_id, guild_id, self.brain_host, self.voice)
        await stream.start()
        self._sessions[session_id] = stream
        logging.info("Started UDP audio session %s", session_id)

    async def stop_session(self, session_id: str):
        stream = self._sessions.pop(session_id, None)
        if stream:
            await stream.stop()
            logging.info("Stopped UDP audio session %s", session_id)

    def push_audio(self, session_id: Optional[str], user_id: int, pcm_data: bytes):
        if session_id and session_id in self._sessions:
            self._sessions[session_id].push_audio(user_id, pcm_data)

    async def stop(self):
        logging.info("Stopping all UDP audio sessions (%d active)", len(self._sessions))
        for sid in list(self._sessions):
            await self.stop_session(sid)
