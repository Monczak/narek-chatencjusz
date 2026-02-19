using System.Collections.Concurrent;
using BrainService.Domain.Session;

namespace BrainService.Services.Session;

internal sealed class VoiceSessionRuntimeState
{
    public readonly VoiceSessionTimer SilenceTimer = new();
    public readonly VoiceSessionTimer GraceTimer = new();
    public readonly VoiceSessionTimer RambleTimer = new();
    public ulong GuildId { get; set; }

    public ConcurrentQueue<VoiceSessionEventDocument> PendingEvents { get; } = new();
}
