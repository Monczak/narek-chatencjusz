using System.Buffers;
using System.Threading.Channels;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using BrainService.Domain.Audio;

namespace BrainService.Services.Audio.Nodes;

public class MixerNode(ILogger<MixerNode> logger) : IAudioNode
{
    private readonly Channel<AudioFrame> _output = Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(2)
    {
        FullMode = BoundedChannelFullMode.Wait
    });
    
    private readonly ConcurrentDictionary<string, (ChannelReader<AudioFrame> Reader, List<float> Buffer)> _inputs = new();

    private const int FrameSize = 960 * 2; 
    
    public ChannelReader<AudioFrame> Output => _output.Reader;

    public void AddInput(string name, ChannelReader<AudioFrame> input)
    {
        _inputs.TryAdd(name, (input ?? throw new ArgumentNullException(nameof(input)), new List<float>(FrameSize * 2)));
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
        
        var mixBuffer = ArrayPool<float>.Shared.Rent(FrameSize);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
        
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                Array.Clear(mixBuffer, 0, FrameSize);
                var timestamp = DateTime.UtcNow;
                
                foreach (var inputData in _inputs.Values)
                {
                    while (inputData.Reader.TryRead(out var frame))
                    {
                        inputData.Buffer.AddRange(frame.Samples.Span);
                        if (frame.Timestamp > timestamp) timestamp = frame.Timestamp;
                        
                        // CRITICAL: Return the input frame's array to the pool!
                        // This prevents memory leaks since BotSourceNode is now Renting.
                        if (MemoryMarshal.TryGetArray(frame.Samples, out var segment) && segment.Array != null)
                        {
                            ArrayPool<float>.Shared.Return(segment.Array);
                        }
                    }
                    
                    var samplesAvailable = inputData.Buffer.Count;
                    var samplesToMix = Math.Min(samplesAvailable, FrameSize);
                    
                    if (samplesToMix > 0)
                    {
                        var span = CollectionsMarshal.AsSpan(inputData.Buffer);
                        for (int i = 0; i < samplesToMix; i++)
                        {
                            mixBuffer[i] = Math.Clamp(mixBuffer[i] + span[i], -1f, 1f);
                        }
                        inputData.Buffer.RemoveRange(0, samplesToMix);
                    }
                }
                
                // Always produce a frame (silence if no inputs had data)
                // This is important for maintaining timing
                var outputFrame = new AudioFrame
                {
                    Samples = mixBuffer.AsMemory(0, FrameSize),
                    Timestamp = timestamp
                };
                
                // This blocks if sink isn't ready (backpressure)
                await _output.Writer.WriteAsync(outputFrame, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        finally
        {
            ArrayPool<float>.Shared.Return(mixBuffer);
            _output.Writer.Complete();
            logger.LogInformation("MixerNode stopped");
        }
    }
}
