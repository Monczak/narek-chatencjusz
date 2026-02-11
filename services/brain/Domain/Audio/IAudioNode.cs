using System.Threading.Channels;

namespace BrainService.Domain.Audio;


public interface IAudioNode
{
    ChannelReader<AudioFrame> Output { get; }
    
    Task StartAsync(CancellationToken ct);
}