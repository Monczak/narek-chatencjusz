using System.Threading.Channels;

namespace BrainService.Domain.Audio;

public interface IAudioNode
{
    ChannelReader<AudioFrame> Output { get; }
    
    int QueueDepth => Output.Count;
    
    Task StartAsync(CancellationToken ct);
}