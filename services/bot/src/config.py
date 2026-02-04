import os

from dotenv import load_dotenv
load_dotenv()

class Config:
    def __init__(self):
        self.brain_url = os.getenv("BRAIN_URL", "localhost:5050")
        self.discord_bot_token = os.getenv("DISCORD_BOT_TOKEN")

config = Config()