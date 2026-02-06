import contextlib
import uuid
import discord
from typing import Dict, Generator, Optional, Tuple

class InteractionService:
    def __init__(self):
        self._pending: Dict[str, discord.ApplicationContext] = {}

    def register(self, ctx: discord.ApplicationContext) -> str:
        correlation_id = uuid.uuid4().hex
        self._pending[correlation_id] = ctx
        return correlation_id

    def pop(self, correlation_id: str | None) -> Optional[discord.ApplicationContext]:
        if not correlation_id:
            return None
        return self._pending.pop(correlation_id, None)
    
    def discard(self, correlation_id: str | None) -> None:
        if not correlation_id:
            return
        self._pending.pop(correlation_id, None)

    @contextlib.contextmanager
    def long_interaction(self, ctx: discord.ApplicationContext):
        correlation_id = self.register(ctx)
        state = {"commit": False}

        class Handle:
            def keep(self): state["commit"] = True

        try:
            yield correlation_id, Handle()
        except Exception:
            self.discard(correlation_id)
            raise
        finally:
            if not state["commit"]:
                self.discard(correlation_id)
