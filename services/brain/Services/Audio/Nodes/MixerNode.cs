using System.Buffers;
using System.Threading.Channels;
using System.Runtime.InteropServices;
// using System.Collections.Concurrent; // REMOVE THIS
using System.Diagnostics;
using BrainService.Domain.Audio;

namespace BrainService.Services.Audio.Nodes;

public class MixerNode(ILogger<MixerNode> logger) : IAudioNode
{
    // OPTIMIZATION: Use SingleReader/SingleWriter to reduce Channel allocations
    private readonly Channel<AudioFrame> _output = Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(50)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = true
    });
    
    // FIX: Use Dictionary + Lock instead of ConcurrentDictionary to avoid Enumerator allocations
    private readonly Dictionary<string, (ChannelReader<AudioFrame> Reader, List<float> Buffer)> _inputs = new();
    private readonly object _lock = new();

    private const int FrameSize = 960 * 2; 

    public ChannelReader<AudioFrame> Output => _output.Reader;

    public void AddInput(string name, ChannelReader<AudioFrame> input)
    {
        lock (_lock)
        {
            if (_inputs.TryAdd(name, (input ?? throw new ArgumentNullException(nameof(input)), new List<float>(FrameSize * 5))))
            {
                logger.LogInformation("Added input '{Name}' to mixer", name);
            }
        }
    }
    
    public void RemoveInput(string name)
    {
        lock (_lock)
        {
            if (_inputs.Remove(name))
            {
                logger.LogInformation("Removed input '{Name}' from mixer", name);
            }
        }
    }
    
    // ... StartAsync and RunTimingLoop remain the same ...
    public async Task StartAsync(CancellationToken ct)
    {
        await RunTimingLoop(ct);
    }

    private async Task RunTimingLoop(CancellationToken ct)
    {
        logger.LogInformation("MixerNode started on dedicated thread");
    
        // PeriodicTimer automatically corrects for drift and is more efficient than Sleep/SpinWait
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));

        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                ProcessTick();
            }
        }
        catch (OperationCanceledException) 
        {
            // Ignore
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "MixerNode timing loop error");
            throw;
        }
        finally
        {
            _output.Writer.TryComplete();
        }
    }
    
    private void ProcessTick()
    {
        // FIX: Lock the dictionary during iteration. 
        // Dictionary<TKey, TValue>.Enumerator is a struct, so this Foreach is Zero-Allocation.
        lock (_lock) 
        {
            foreach (var kvp in _inputs)
            {
                var inputData = kvp.Value;
                while (inputData.Reader.TryRead(out var frame))
                {
                    inputData.Buffer.AddRange(frame.Samples.Span);
                    if (MemoryMarshal.TryGetArray(frame.Samples, out var seg) && seg.Array != null)
                        ArrayPool<float>.Shared.Return(seg.Array);
                }
            }
        }

        var mixBuffer = ArrayPool<float>.Shared.Rent(FrameSize);
        Array.Clear(mixBuffer, 0, FrameSize);

        lock (_lock)
        {
            foreach (var kvp in _inputs)
            {
                var inputData = kvp.Value;
                var available = Math.Min(inputData.Buffer.Count, FrameSize);
                if (available <= 0) continue;

                var span = CollectionsMarshal.AsSpan(inputData.Buffer);
                for (var i = 0; i < available; i++)
                    mixBuffer[i] = Math.Clamp(mixBuffer[i] + span[i], -1f, 1f);
                inputData.Buffer.RemoveRange(0, available);
            }
        }

        var newFrame = new AudioFrame
        {
            Samples = mixBuffer.AsMemory(0, FrameSize),
            Timestamp = DateTime.UtcNow
        };

        if (!_output.Writer.TryWrite(newFrame))
        {
             // Manual Drop logic (Keep this from previous fix!)
            if (_output.Reader.TryRead(out var droppedFrame))
            {
                if (MemoryMarshal.TryGetArray(droppedFrame.Samples, out var segment) && segment.Array != null)
                    ArrayPool<float>.Shared.Return(segment.Array);
                logger.LogWarning("MixerNode: Dropped frame to maintain realtime");
            }

            if (!_output.Writer.TryWrite(newFrame))
            {
                ArrayPool<float>.Shared.Return(mixBuffer);
            }
        }
    }
}
