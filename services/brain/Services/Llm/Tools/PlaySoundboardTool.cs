using System.ComponentModel;
using BrainService.Services.Audio;
using BrainService.Services.Audio.Graph;
using Microsoft.Extensions.AI;

namespace BrainService.Services.Llm.Tools;

public class PlaySoundboardTool(
    SoundboardService soundboardService,
    AudioGraphFactory audioGraphFactory,
    ToolContextAccessor contextAccessor) : IToolExecutor
{
    public AIFunction AIFunction { get; } = AIFunctionFactory.Create(
        (
            [Description("The exact name of the sound to play, as listed in the available sounds")] string sound_name) =>
        {
            var ctx = contextAccessor.Current ?? throw new InvalidOperationException("Tool context not set.");

            var samples = soundboardService.TryGetSamples(sound_name.Trim());
            if (samples == null)
                return Task.FromResult($"Sound '{sound_name}' not found.");

            var node = audioGraphFactory.TryGetSoundboard(ctx.SessionId);
            if (node == null)
                return Task.FromResult("Session is not active.");

            var queued = node.Enqueue(samples);
            return Task.FromResult(queued
                ? $"Playing '{sound_name}'."
                : $"Playing '{sound_name}' (buffer was full, some frames dropped).");
        },
        name: "play_soundboard",
        description: "Play a soundboard sound in the voice channel.");
}
