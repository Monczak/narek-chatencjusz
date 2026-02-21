using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using BrainService.Domain.Audio;

namespace BrainService.Services.Audio.Nodes;

public sealed class TtsNode : IAudioNode
{
    private readonly ConcurrentQueue<(float[] Buf, int Length)> _buffer = new();
    private int _bufferedFrames;
    
    private readonly Channel<AudioFrame> _output = Channel.CreateBounded<AudioFrame>(
        new BoundedChannelOptions(2)
        {
            FullMode     = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });

    private const int FrameSamples = 1920; // 20ms stereo 48kHz float

    public ChannelReader<AudioFrame> Output => _output.Reader;
    
    public int QueueDepth => _bufferedFrames;
    
    public void Enqueue(float[] samples)
    {
        for (var offset = 0; offset < samples.Length; offset += FrameSamples)
        {
            var frameLen = Math.Min(FrameSamples, samples.Length - offset);
            var buf = ArrayPool<float>.Shared.Rent(FrameSamples);
            buf.AsSpan(0, FrameSamples).Clear();
            samples.AsSpan(offset, frameLen).CopyTo(buf.AsSpan());
            _buffer.Enqueue((buf, FrameSamples));
            Interlocked.Increment(ref _bufferedFrames);
        }
    }
    
    public void Flush()
    {
        while (_buffer.TryDequeue(out var entry))
        {
            ArrayPool<float>.Shared.Return(entry.Buf);
            Interlocked.Decrement(ref _bufferedFrames);
        }
    }

    public async Task StartAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));

            while (await timer.WaitForNextTickAsync(ct))
            {
                if (!_buffer.TryDequeue(out var entry))
                    continue;

                Interlocked.Decrement(ref _bufferedFrames);

                var frame = new AudioFrame
                {
                    Samples   = entry.Buf.AsMemory(0, entry.Length),
                    Timestamp = DateTime.UtcNow,
                };
                
                await _output.Writer.WriteAsync(frame, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown - return any remaining buffered frames to the pool
            Flush();

            while (_output.Reader.TryRead(out var frame))
            {
                if (MemoryMarshal.TryGetArray(frame.Samples, out var seg) && seg.Array != null)
                    ArrayPool<float>.Shared.Return(seg.Array);
            }
        }
        finally
        {
            _output.Writer.TryComplete();
        }
    }
}
