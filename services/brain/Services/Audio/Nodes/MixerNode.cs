using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using BrainService.Domain.Audio;

namespace BrainService.Services.Audio.Nodes;

/// <summary>
/// Mixes N input streams into one output at a fixed 20 ms tick rate.
/// Runs on a dedicated OS thread so it is immune to thread-pool starvation.
/// The timing loop always targets the *next future* 20 ms boundary so an
/// overslept Sleep never causes burst emission of multiple frames.
/// </summary>
public sealed class MixerNode(ILogger<MixerNode> logger) : IAudioNode
{
    // Output channel — DropOldest so a slow sink never stalls the timing thread.
    private readonly Channel<AudioFrame> _output = Channel.CreateBounded<AudioFrame>(
        new BoundedChannelOptions(50) { FullMode = BoundedChannelFullMode.DropOldest });

    // Input registry — plain Dictionary + lock to avoid ConcurrentDictionary enumerator allocations.
    private readonly Dictionary<string, (ChannelReader<AudioFrame> Reader, List<float> Buffer)> _inputs = new();
    private readonly Lock _inputsLock = new();

    // 20 ms of stereo 48 kHz float = 960 * 2 = 1920 samples
    private const int FrameSamples = 1920;

    public ChannelReader<AudioFrame> Output => _output.Reader;

    // ── input management ─────────────────────────────────────────────────────
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

    // ── IAudioNode ───────────────────────────────────────────────────────────
    public Task StartAsync(CancellationToken ct)
    {
        // Block the calling thread (which should already be a dedicated OS thread
        // started via Task.Factory.StartNew with TaskCreationOptions.LongRunning).
        TimingLoop(ct);
        _output.Writer.TryComplete();
        return Task.CompletedTask;
    }

    // ── timing loop ──────────────────────────────────────────────────────────
    private void TimingLoop(CancellationToken ct)
    {
        logger.LogInformation("MixerNode timing loop started on thread '{Name}'",
            Thread.CurrentThread.Name ?? "unnamed");

        var sw = Stopwatch.StartNew();

        while (!ct.IsCancellationRequested)
        {
            // ── compute the next 20 ms boundary strictly in the future ────────
            // Using TotalMicroseconds for precision; Math.Floor avoids floating-
            // point drift accumulation over long sessions.
            var elapsedUs = sw.Elapsed.TotalMicroseconds;
            var nextUs = (Math.Floor(elapsedUs / 20_000.0) + 1.0) * 20_000.0;

            // ── coarse sleep with a small margin for the spin phase ───────────
            double sleepUs = nextUs - elapsedUs - 1_500.0; // 1.5 ms spin margin
            // logger.LogInformation("{Time} Sleep for {sleepUs}", ((DateTimeOffset)DateTime.UtcNow).ToUnixTimeMilliseconds(), sleepUs);
            if (sleepUs > 0)
                Thread.Sleep((int)(sleepUs / 1000.0));

            // ── spin-wait for the exact boundary ─────────────────────────────
            // logger.LogInformation("{Time} Spin Wait Started", ((DateTimeOffset)DateTime.UtcNow).ToUnixTimeMilliseconds());
            while (sw.Elapsed.TotalMicroseconds < nextUs && !ct.IsCancellationRequested)
                Thread.SpinWait(10);

            // logger.LogInformation("{Time} ProcessTick Started", ((DateTimeOffset)DateTime.UtcNow).ToUnixTimeMilliseconds());
            ProcessTick();
            // logger.LogInformation("{Time} ProcessTick Ended", ((DateTimeOffset)DateTime.UtcNow).ToUnixTimeMilliseconds());
        }
    }

    // ── per-tick work ─────────────────────────────────────────────────────────
    private void ProcessTick()
    {
        // Drain all pending frames from every input into per-input sample buffers.
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

        // Mix exactly FrameSamples from each input's buffer into a rented output buffer.
        var mix = ArrayPool<float>.Shared.Rent(FrameSamples);
        mix.AsSpan(0, FrameSamples).Clear();
        
        lock (_inputsLock)
        {
            foreach (var (_, (_, buf)) in _inputs)
            {
                int avail = Math.Min(buf.Count, FrameSamples);
                if (avail == 0) continue;

                var span = CollectionsMarshal.AsSpan(buf);
                for (int i = 0; i < avail; i++)
                    mix[i] = Math.Clamp(mix[i] + span[i], -1f, 1f);

                buf.RemoveRange(0, avail);
            }
        } 

        var outFrame = new AudioFrame { Samples = mix.AsMemory(0, FrameSamples), Timestamp = DateTime.UtcNow };

        // TryWrite — DropOldest mode handles a full channel automatically,
        // but we still need to return the dropped frame's buffer.
        if (!_output.Writer.TryWrite(outFrame))
            ArrayPool<float>.Shared.Return(mix);
    }

    private static void ReturnFrame(AudioFrame frame)
    {
        if (MemoryMarshal.TryGetArray(frame.Samples, out var seg) && seg.Array != null)
            ArrayPool<float>.Shared.Return(seg.Array);
    }
}
