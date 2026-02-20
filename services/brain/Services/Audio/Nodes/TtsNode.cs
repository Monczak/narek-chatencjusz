using System.Buffers;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using BrainService.Domain.Audio;

namespace BrainService.Services.Audio.Nodes;

public class TtsNode
{
    private readonly Channel<AudioFrame> _channel = Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(4000)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
    });

    private const int FrameSamples = 1920; // 20 ms stereo 48 kHz float

    public ChannelReader<AudioFrame> Output => _channel.Reader;
    
    public int QueueDepth => _channel.Reader.Count;

    public void Enqueue(float[] samples)
    {
        for (var offset = 0; offset < samples.Length; offset += FrameSamples)
        {
            var frameLen = Math.Min(FrameSamples, samples.Length - offset);
            var buf = ArrayPool<float>.Shared.Rent(FrameSamples);
            buf.AsSpan(0, FrameSamples).Clear();
            samples.AsSpan(offset, frameLen).CopyTo(buf.AsSpan());

            var frame = new AudioFrame
            {
                Samples   = buf.AsMemory(0, FrameSamples),
                Timestamp = DateTime.UtcNow,
            };

            if (!_channel.Writer.TryWrite(frame))
                ArrayPool<float>.Shared.Return(buf);
        }
    }
    
    public void Flush()
    {
        while (_channel.Reader.TryRead(out var frame))
        {
            if (MemoryMarshal.TryGetArray(frame.Samples, out var seg) && seg.Array != null)
                ArrayPool<float>.Shared.Return(seg.Array);
        }
    }
}
