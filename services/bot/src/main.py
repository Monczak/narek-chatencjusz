import discord
import os
import logging

from dotenv import load_dotenv
load_dotenv()

logging.basicConfig(level=logging.INFO)

TOKEN = os.getenv("DISCORD_BOT_TOKEN")

with open(".version", "r") as version_file:
    VERSION = version_file.read().strip()

bot = discord.Bot()

@bot.event
async def on_ready():
    logging.info(f"Logged in as {bot.user}")
    logging.info(f"Narek Chatencjusz bot service is up and running")

@bot.slash_command(name="ping", description="Ping")
async def ping(ctx: discord.ApplicationContext):
    await ctx.respond("Pong!")

def main():
    logging.info(f"Starting Narek Chatencjusz bot service - version {VERSION}")
    bot.run(TOKEN)

if __name__ == "__main__":
    main()
