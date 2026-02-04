import discord
import os
import logging

from config import config
from test_grpc import ping_brain

from dotenv import load_dotenv
load_dotenv()

logging.basicConfig(level=logging.INFO)

TOKEN = config.discord_bot_token

with open(".version", "r") as version_file:
    VERSION = version_file.read().strip()

bot = discord.Bot()

@bot.event
async def on_ready():
    logging.info(f"Logged in as {bot.user}")
    logging.info(f"Narek Chatencjusz bot service is up and running")

@bot.slash_command(name="ping", description="Ping")
async def ping(ctx: discord.ApplicationContext):
    response = ping_brain()
    await ctx.respond(f"Pong! Response: {response.message}")

def main():
    logging.info(f"Starting Narek Chatencjusz bot service - version {VERSION}")
    bot.run(TOKEN)

if __name__ == "__main__":
    main()
