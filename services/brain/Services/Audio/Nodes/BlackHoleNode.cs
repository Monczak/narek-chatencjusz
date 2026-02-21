using System.Buffers;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using BrainService.Domain.Audio;

namespace BrainService.Services.Audio.Nodes;

public class BlackHoleNode(ChannelReader<AudioFrame> input) : IAudioNode
{
    private readonly Channel<AudioFrame> _output = Channel.CreateBounded<AudioFrame>(1);

    public ChannelReader<AudioFrame> Output => _output.Reader;
    public int QueueDepth => 0;

    public async Task StartAsync(CancellationToken ct)
    {
        _output.Writer.TryComplete();

        try
        {
            await foreach (var frame in input.ReadAllAsync(ct))
            {
                if (MemoryMarshal.TryGetArray(frame.Samples, out var seg) && seg.Array != null)
                    ArrayPool<float>.Shared.Return(seg.Array);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
    }
}
