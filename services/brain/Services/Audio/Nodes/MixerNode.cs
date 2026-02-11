using System.Buffers;
using System.Threading.Channels;
using BrainService.Domain.Audio;

namespace BrainService.Services.Audio.Nodes;

public class MixerNode(ILogger<MixerNode> logger) : IAudioNode
{
    private readonly Channel<AudioFrame> _output = Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(2)
    {
        FullMode = BoundedChannelFullMode.Wait
    });
    private readonly Dictionary<string, ChannelReader<AudioFrame>> _inputs = new();

    // 48kHz stereo, 20ms frames
    private const int FrameSize = 960 * 2; // 1920 samples (960 per channel)
    
    public ChannelReader<AudioFrame> Output => _output.Reader;

    public void AddInput(string name, ChannelReader<AudioFrame> input)
    {
        _inputs[name] = input ?? throw new ArgumentNullException(nameof(input));
        logger.LogInformation("Added input '{Name}' to mixer", name);
    }
    
    public void RemoveInput(string name)
    {
        if (_inputs.Remove(name))
        {
            logger.LogInformation("Removed input '{Name}' from mixer", name);
        }
    }
    
    public async Task StartAsync(CancellationToken ct)
    {
        logger.LogInformation("MixerNode started with {Count} inputs", _inputs.Count);
        
        var mixBuffer = ArrayPool<float>.Shared.Rent(FrameSize);
        
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Array.Clear(mixBuffer, 0, FrameSize);
                var timestamp = DateTime.UtcNow;
                
                // Pull from all available inputs and mix
                foreach (var (name, input) in _inputs.ToArray()) // ToArray to avoid modification issues
                {
                    // Non-blocking read - if no data available, contribute silence
                    if (input.TryRead(out var frame))
                    {
                        var span = frame.Samples.Span;
                        var samplesToMix = Math.Min(span.Length, FrameSize);
                        
                        // Mix by summing and clamping
                        for (int i = 0; i < samplesToMix; i++)
                        {
                            mixBuffer[i] = Math.Clamp(mixBuffer[i] + span[i], -1f, 1f);
                        }
                        
                        // Use most recent timestamp
                        if (frame.Timestamp > timestamp)
                        {
                            timestamp = frame.Timestamp;
                        }
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
            logger.LogInformation("MixerNode cancelled");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "MixerNode error");
            throw;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(mixBuffer);
            _output.Writer.Complete();
            logger.LogInformation("MixerNode stopped");
        }
    }
}
