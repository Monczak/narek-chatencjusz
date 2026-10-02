# Narek Chatencjusz

## What is this?

Narek Chatencjusz is a conversational Discord bot. It can join voice calls and talk with users in real time, and use tools to make conversations more fun and immersive.

The entire system can be run locally, without dependence on 3rd party APIs.

## Overview

The bot comprises several services:

- **bot** - a Discord gateway/frontend developed in Python using Pycord, relaying commands to the brain and streaming audio from the brain
- **brain** - an ASP.NET Core application responsible for:
  - the bot's logic
  - conversation flow
  - state management
  - audio processing (mixing, soundboard, voice activity detection with Silero VAD, experimental DSP effects)
  - configuration (global and per guild)
  - a web-based real-time dashboard, using Blazor and MudBlazor, powered by SignalR
- **asr** - a small Python service used for automatic speech recognition using `faster-whisper` (PyTorch, CUDA)
- **llm** - Ollama with a customizable `Modelfile`
- **mongo** - a MongoDB instance for storing bot configuration and conversation history
- **valkey** - a Valkey instance used for bot-brain pub/sub, state reconciliation and health checking

All of the services above run in Docker. The **bot**, **brain** and **asr** services communicate via gRPC, mostly because I wanted to try these out to see how they compare to REST API.

The code also makes use of an external service for text-to-speech, which in this case is a small SAPI wrapper from another project of mine to be run on a Windows host. This was done simply because I quite like one very specific SAPI voice (Microsoft Paulina) for how silly and utterly broken it is.

### Why separate the bot and brain services?

Three major reasons:

1. I felt Python wasn't exactly suitable for real-time audio DSP, especially with Pycord's event loop running, and this was one key feature I wanted to explore.
2. There were several other features I wanted to explore more in C#, especially the low-level memory management ones (like shared array pools, `Span<T>` and low-alloc code principles).
3. I wanted to try out some distributed computing principles I learned in uni, but in practice, with a project more on the ambitious side.

### Is this overengineered?

Definitely. Applying distributed architecture principles to a project like this ultimately led to (frankly undesirable) complexity and bloat, but I had an excellent opportunity to at least try these concepts out in practice and gain a better understanding of everything.

## AI Disclosure

Parts of this bot's code were implemented with AI assistance, especially those responsible for audio streaming, the conversation flow and the dashboard. This was to help explore these domains, as well as to see what was possible with current agentic coding tools, given precise requirements, testing and iterating. The overall system design, as well as the approaches used (and consequently, the _ultimately dubious_ decisions), were worked out by myself.
