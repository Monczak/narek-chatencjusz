from typing import List
from pydantic import Field
from pydantic_settings import BaseSettings

class Settings(BaseSettings):
    discord_bot_token: str
    brain_url: str = "localhost:5050"
    valkey_url: str = "valkey://localhost:6379"

    debug_guild_ids_raw: str = Field("", validation_alias="DEBUG_GUILD_IDS")

    class Config:
        env_file = ".env"
        env_file_encoding = "utf-8"

    @property
    def debug_guild_ids(self) -> List[int]:
        s = self.debug_guild_ids_raw.strip()
        if not s:
            return []
        try:
            return [int(x.strip()) for x in s.split(",") if x.strip()]
        except ValueError:
            return []
