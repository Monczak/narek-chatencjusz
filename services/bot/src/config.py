from pydantic_settings import BaseSettings

class Settings(BaseSettings):
    discord_bot_token: str
    brain_url: str = "localhost:5050"
    valkey_url: str = "valkey://localhost:6379"

    class Config:
        env_file = ".env"
        env_file_encoding = "utf-8"
