using System.Buffers;
using System.Threading.Channels;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.Diagnostics;
using BrainService.Domain.Audio;

namespace BrainService.Services.Audio.Nodes;

public class MixerNode(ILogger<MixerNode> logger) : IAudioNode
{
    private readonly Channel<AudioFrame> _output = Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(50)
    {
        FullMode = BoundedChannelFullMode.DropOldest
    });
    
    private readonly ConcurrentDictionary<string, (ChannelReader<AudioFrame> Reader, List<float> Buffer)> _inputs = new();

    private const int FrameSize = 960 * 2; // 20ms at 48kHz stereo

    public ChannelReader<AudioFrame> Output => _output.Reader;

    public void AddInput(string name, ChannelReader<AudioFrame> input)
    {
        _inputs.TryAdd(name, (input ?? throw new ArgumentNullException(nameof(input)), new List<float>(FrameSize * 5)));
        logger.LogInformation("Added input '{Name}' to mixer", name);
    }
    
    public void RemoveInput(string name)
    {
        if (_inputs.TryRemove(name, out _))
        {
            logger.LogInformation("Removed input '{Name}' from mixer", name);
        }
    }
    
    public async Task StartAsync(CancellationToken ct)
    {
        // Delegate to a dedicated thread so the timing loop is
        // never at the mercy of thread pool scheduling.
        await Task.Factory.StartNew(
            () => RunTimingLoop(ct),
            ct,
            TaskCreationOptions.LongRunning, // hints the pool to use a dedicated thread
            TaskScheduler.Default
        );
    }
    
    private void RunTimingLoop(CancellationToken ct)
    {
        logger.LogInformation("MixerNode started on dedicated thread");
        var sw = Stopwatch.StartNew();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Always target the next 20ms boundary strictly in the future.
                // If we overslept and missed one, we skip it rather than catching up.
                var elapsed = sw.Elapsed.TotalMilliseconds;
                var nextBoundary = (Math.Floor(elapsed / 20.0) + 1) * 20.0;

                var sleepMs = nextBoundary - elapsed - 2.0;
                if (sleepMs > 0)
                    Thread.Sleep((int)sleepMs);

                while (sw.Elapsed.TotalMilliseconds < nextBoundary && !ct.IsCancellationRequested)
                    Thread.SpinWait(50);

                ProcessTick();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "MixerNode timing loop error");
            throw;
        }
        finally
        {
            _output.Writer.TryComplete();
            logger.LogInformation("MixerNode stopped");
        }
    }
    
    private void ProcessTick()
    {
        foreach (var inputData in _inputs.Values)
        {
            while (inputData.Reader.TryRead(out var frame))
            {
                inputData.Buffer.AddRange(frame.Samples.Span);
                if (MemoryMarshal.TryGetArray(frame.Samples, out var seg) && seg.Array != null)
                    ArrayPool<float>.Shared.Return(seg.Array);
            }
        }

        var mixBuffer = ArrayPool<float>.Shared.Rent(FrameSize);
        Array.Clear(mixBuffer, 0, FrameSize);

        foreach (var inputData in _inputs.Values)
        {
            var available = Math.Min(inputData.Buffer.Count, FrameSize);
            if (available <= 0) continue;

            var span = CollectionsMarshal.AsSpan(inputData.Buffer);
            for (var i = 0; i < available; i++)
                mixBuffer[i] = Math.Clamp(mixBuffer[i] + span[i], -1f, 1f);
            inputData.Buffer.RemoveRange(0, available);
        }

        var newFrame = new AudioFrame
        {
            Samples = mixBuffer.AsMemory(0, FrameSize),
            Timestamp = DateTime.UtcNow
        };

        if (!_output.Writer.TryWrite(newFrame))
        {
            // Dropped because sink is stalling — return buffer to pool
            ArrayPool<float>.Shared.Return(mixBuffer);
            logger.LogWarning("MixerNode: dropped frame (sink backpressure)");
        }
    }
}
