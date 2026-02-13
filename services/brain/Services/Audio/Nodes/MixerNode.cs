using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using BrainService.Domain.Audio;

namespace BrainService.Services.Audio.Nodes;

public sealed class MixerNode(ILogger<MixerNode> logger) : IAudioNode
{
    private readonly Channel<AudioFrame> _output = Channel.CreateBounded<AudioFrame>(
        new BoundedChannelOptions(50) { FullMode = BoundedChannelFullMode.DropOldest });
    
    private readonly Dictionary<string, (ChannelReader<AudioFrame> Reader, List<float> Buffer)> _inputs = new();
    private readonly Lock _inputsLock = new();
    
    private const int FrameSamples = 1920; // 20 ms of stereo 48 kHz float

    public ChannelReader<AudioFrame> Output => _output.Reader;
    
    public void AddInput(string name, ChannelReader<AudioFrame> reader)
    {
        lock (_inputsLock)
        {
            _inputs.TryAdd(name, (reader, new List<float>(FrameSamples * 4)));
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
        lock (_inputsLock)
        {
            foreach (var (_, (reader, buf)) in _inputs)
            {
                while (reader.TryRead(out var frame))
                {
                    buf.AddRange(frame.Samples.Span);
                    ReturnFrame(frame);
                }
            }
        }
        
        var mix = ArrayPool<float>.Shared.Rent(FrameSamples);
        mix.AsSpan(0, FrameSamples).Clear();
        
        lock (_inputsLock)
        {
            foreach (var (_, (_, buf)) in _inputs)
            {
                var avail = Math.Min(buf.Count, FrameSamples);
                if (avail == 0) continue;

                var span = CollectionsMarshal.AsSpan(buf);
                for (var i = 0; i < avail; i++)
                    mix[i] = Math.Clamp(mix[i] + span[i], -1f, 1f);

                buf.RemoveRange(0, avail);
            }
        } 

        var outFrame = new AudioFrame { Samples = mix.AsMemory(0, FrameSamples), Timestamp = DateTime.UtcNow };
        
        if (!_output.Writer.TryWrite(outFrame))
            ArrayPool<float>.Shared.Return(mix);
    }

    private static void ReturnFrame(AudioFrame frame)
    {
        if (MemoryMarshal.TryGetArray(frame.Samples, out var seg) && seg.Array != null)
            ArrayPool<float>.Shared.Return(seg.Array);
    }
}
