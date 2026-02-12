namespace BrainService.Domain.Audio;

public readonly struct AudioFrame
{
    public ReadOnlyMemory<float> Samples { get; init; }
    public ulong UserId { get; init; }
    public DateTime Timestamp { get; init; }
}