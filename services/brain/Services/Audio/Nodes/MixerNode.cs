using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using BrainService.Domain.Audio;

namespace BrainService.Services.Audio.Nodes;

public sealed class MixerNode(ILogger<MixerNode> logger) : IAudioNode
{
    private readonly Channel<AudioFrame> _output = Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(50)
    {
        FullMode = BoundedChannelFullMode.DropOldest
    });
    
    private sealed class MixerState
    {
        public required ChannelReader<AudioFrame> Reader { get; init; }
        public List<float> Buffer { get; } = new(FrameSamples * 4);
        public DateTime LastTimestamp { get; set; } = DateTime.UtcNow;
    }
    
    private readonly Dictionary<string, MixerState> _inputs = new();
    private readonly Lock _inputsLock = new();
    
    private const int FrameSamples = 1920; // 20 ms of stereo 48 kHz float

    public ChannelReader<AudioFrame> Output => _output.Reader;
    
    public void AddInput(string name, ChannelReader<AudioFrame> reader)
    {
        lock (_inputsLock)
        {
            _inputs.TryAdd(name, new MixerState { Reader = reader });
            logger.LogDebug("Mixer: added input '{Name}'", name);
        }
    }

    public void RemoveInput(string name)
    {
        lock (_inputsLock)
        {
            _inputs.Remove(name);
            logger.LogDebug("Mixer: removed input '{Name}'", name);
        }
    }
    
    public Task StartAsync(CancellationToken ct)
    {
        TimingLoop(ct);
        _output.Writer.TryComplete();
        return Task.CompletedTask;
    }
    
    private void TimingLoop(CancellationToken ct)
    {
        logger.LogInformation("MixerNode timing loop started on thread '{Name}'",
            Thread.CurrentThread.Name ?? "unnamed");

        var sw = Stopwatch.StartNew();

        while (!ct.IsCancellationRequested)
        {
            var elapsedUs = sw.Elapsed.TotalMicroseconds;
            var nextUs = (Math.Floor(elapsedUs / 20_000.0) + 1.0) * 20_000.0;
            
            var sleepUs = nextUs - elapsedUs - 1_500.0; // 1.5 ms spin margin
            if (sleepUs > 0)
                Thread.Sleep((int)(sleepUs / 1000.0));
            
            while (sw.Elapsed.TotalMicroseconds < nextUs && !ct.IsCancellationRequested)
                Thread.SpinWait(10);
            
            ProcessTick();
        }
    }
    
    private void ProcessTick()
    {
        DateTime? oldestTimestamp = null;

        lock (_inputsLock)
        {
            foreach (var input in _inputs.Values)
            {
                while (input.Reader.TryRead(out var frame))
                {
                    input.LastTimestamp = frame.Timestamp;
                    input.Buffer.AddRange(frame.Samples.Span);
                    ReturnFrame(frame);
                }

                if (input.Buffer.Count > 0)
                {
                    // Find the oldest timestamp among all active buffers
                    if (oldestTimestamp == null || input.LastTimestamp < oldestTimestamp)
                        oldestTimestamp = input.LastTimestamp;
                }
            }
        }
        
        var mix = ArrayPool<float>.Shared.Rent(FrameSamples);
        mix.AsSpan(0, FrameSamples).Clear();
        
        lock (_inputsLock)
        {
            foreach (var input in _inputs.Values)
            {
                var avail = Math.Min(input.Buffer.Count, FrameSamples);
                if (avail == 0) continue;

                var span = CollectionsMarshal.AsSpan(input.Buffer);
                for (var i = 0; i < avail; i++)
                    mix[i] = Math.Clamp(mix[i] + span[i], -1f, 1f);

                input.Buffer.RemoveRange(0, avail);
            }
        }

        var outFrame = new AudioFrame 
        { 
            Samples = mix.AsMemory(0, FrameSamples), 
            Timestamp = oldestTimestamp ?? DateTime.UtcNow 
        };

        if (!_output.Writer.TryWrite(outFrame))
        {
            ArrayPool<float>.Shared.Return(mix);
        }
    }

    private static void ReturnFrame(AudioFrame frame)
    {
        if (MemoryMarshal.TryGetArray(frame.Samples, out var seg) && seg.Array != null)
        {
            ArrayPool<float>.Shared.Return(seg.Array);
        }
    }
}
