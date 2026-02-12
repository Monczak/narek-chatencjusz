using System.Buffers;
using System.Threading.Channels;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using BrainService.Domain.Audio;

namespace BrainService.Services.Audio.Nodes;

public class MixerNode(ILogger<MixerNode> logger) : IAudioNode
{
    private readonly Channel<AudioFrame> _output = Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(50)
    {
        FullMode = BoundedChannelFullMode.Wait
    });
    
    private readonly ConcurrentDictionary<string, (ChannelReader<AudioFrame> Reader, List<float> Buffer)> _inputs = new();

    private const int FrameSize = 960 * 2; // 20ms at 48kHz stereo
    
    // Hysteresis Configuration
    private const int StartThreshold = 16;   // Wait for 60ms of audio before starting playback (Prevents crackling)
    private const int CatchUpThreshold = 50; // If buffer > 200ms, play faster to reduce latency
    
    private bool _isBuffering = true; // Start in buffering mode to build initial reserve

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
        logger.LogInformation("MixerNode started");
        
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
        
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                // 1. Ingest all pending data from all inputs
                foreach (var inputData in _inputs.Values)
                {
                    while (inputData.Reader.TryRead(out var frame))
                    {
                        inputData.Buffer.AddRange(frame.Samples.Span);
                        if (MemoryMarshal.TryGetArray(frame.Samples, out var segment) && segment.Array != null)
                            ArrayPool<float>.Shared.Return(segment.Array);
                    }
                }

                // 2. Determine Buffer Health
                int maxBufferCount = 0;
                foreach (var inputData in _inputs.Values)
                {
                    if (inputData.Buffer.Count > maxBufferCount)
                        maxBufferCount = inputData.Buffer.Count;
                }

                int framesAvailable = maxBufferCount / FrameSize;
                int framesToMix = 0;

                // 3. State Machine: Hysteresis Logic
                if (_isBuffering)
                {
                    // We are starving. Wait until we have enough data (StartThreshold)
                    if (framesAvailable >= StartThreshold)
                    {
                        _isBuffering = false;
                        framesToMix = 1; // Start playing!
                        logger.LogDebug("Buffer recovered. Resuming playback.");
                    }
                    else
                    {
                        framesToMix = 0; // Keep emitting silence while we recharge
                    }
                }
                else
                {
                    // We are playing.
                    if (framesAvailable == 0)
                    {
                        // Underrun detected! Switch to buffering to prevent stutter.
                        _isBuffering = true;
                        framesToMix = 0;
                        logger.LogDebug("Mixer underrun. Re-buffering...");
                    }
                    else if (framesAvailable >= CatchUpThreshold)
                    {
                        // Latency is getting high (>200ms). Speed up to drain it.
                        framesToMix = 2;
                        logger.LogDebug("Catch up threshold passed");
                    }
                    else
                    {
                        // Healthy state
                        framesToMix = 1;
                        // logger.LogDebug("Healthy");
                    }
                }

                // 4. Process Frames (or emit silence)
                // If framesToMix is 0 (Buffering/Starved), we MUST still emit one silence frame
                // to keep the downstream Bot UDP connection alive.
                int loopCount = framesToMix == 0 ? 1 : framesToMix;
                bool isSilence = framesToMix == 0;

                for (int f = 0; f < loopCount; f++)
                {
                    var mixBuffer = ArrayPool<float>.Shared.Rent(FrameSize);
                    Array.Clear(mixBuffer, 0, FrameSize);

                    if (!isSilence)
                    {
                        foreach (var inputData in _inputs.Values)
                        {
                            var samplesAvailable = inputData.Buffer.Count;
                            var samplesToMix = Math.Min(samplesAvailable, FrameSize);
                            
                            if (samplesToMix > 0)
                            {
                                var span = CollectionsMarshal.AsSpan(inputData.Buffer);
                                for (var i = 0; i < samplesToMix; i++)
                                    mixBuffer[i] = Math.Clamp(mixBuffer[i] + span[i], -1f, 1f);
                                inputData.Buffer.RemoveRange(0, samplesToMix);
                            }
                        }
                    }

                    var outputFrame = new AudioFrame
                    {
                        Samples = mixBuffer.AsMemory(0, FrameSize),
                        Timestamp = DateTime.UtcNow
                    };
                    
                    await _output.Writer.WriteAsync(outputFrame, ct);
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _output.Writer.Complete();
            logger.LogInformation("MixerNode stopped");
        }
    }
}
