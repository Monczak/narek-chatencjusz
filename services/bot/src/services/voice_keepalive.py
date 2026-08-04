import asyncio
import logging
import time
from typing import Dict
import discord

# 20ms of PCM silence (stereo, 48kHz, 16-bit)
# 48000 Hz * 0.02 sec = 960 samples per channel, 1920 total samples
# Each sample is 2 bytes (int16), so 3840 bytes total
SILENCE_FRAME = b'\x00\x00' * 1920

# Send silence every 60 seconds if no real audio sent
KEEPALIVE_INTERVAL = 60

class VoiceKeepaliveService:    
    def __init__(self, bot: discord.Bot):
        self.bot = bot
        self._last_sent: Dict[int, float] = {}  # guild_id -> timestamp
        self._running = False
        self._task: asyncio.Task | None = None
        
        logging.info("VoiceKeepaliveService initialized")
    
    async def start(self):
        if not self._running:
            self._running = True
            self._task = asyncio.create_task(self._keepalive_loop())
    
    async def stop(self):
        self._running = False
        if self._task:
            self._task.cancel()
            try:
                await self._task
            except asyncio.CancelledError:
                pass
    
    def mark_audio_sent(self, guild_id: int):
        self._last_sent[guild_id] = time.time()
    
    async def _keepalive_loop(self):
        while self._running:
            try:
                now = time.time()
                
                for vp in self.bot.voice_clients:
                    vc: discord.VoiceClient = vp # type: ignore
                    if not vc.is_connected():
                        continue
                    
                    guild_id = vc.guild.id # type: ignore
                    last_sent = self._last_sent.get(guild_id, 0)
                    
                    # If we haven't sent audio in KEEPALIVE_INTERVAL seconds, send silence
                    if now - last_sent > KEEPALIVE_INTERVAL:
                        try:
                            if not isinstance(getattr(vc, 'encoder', None), discord.opus.Encoder):
                                vc.encoder = discord.opus.Encoder()
                            vc.send_audio_packet(SILENCE_FRAME, encode=True)
                            self._last_sent[guild_id] = now
                            logging.debug(f"Sent keepalive silence to guild {guild_id}")
                        except Exception as e:
                            logging.warning(f"Failed to send keepalive to guild {guild_id}: {e}")
                
                await asyncio.sleep(10)
                
            except asyncio.CancelledError:
                break
            except Exception as e:
                logging.error(f"Error in keepalive loop: {e}")
                await asyncio.sleep(5)
        
        logging.info("Keepalive loop exited")
