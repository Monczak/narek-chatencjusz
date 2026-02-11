using System.Buffers;
using System.Threading.Channels;
using BrainService.Domain.Audio;

namespace BrainService.Services.Audio.Nodes;

public class ChannelConverterNode : IAudioNode
{
    private readonly Channel<AudioFrame> _output;
    private readonly ChannelReader<AudioFrame> _input;
    private readonly bool _monoToStereo;
    private readonly ILogger<ChannelConverterNode> _logger;
    
    public ChannelReader<AudioFrame> Output => _output.Reader;
    
    public ChannelConverterNode(
        ChannelReader<AudioFrame> input,
        bool monoToStereo,
        ILogger<ChannelConverterNode> logger)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _monoToStereo = monoToStereo;
        _logger = logger;
        
        _output = Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(4)
        {
            FullMode = BoundedChannelFullMode.Wait
        });
        
        _logger.LogInformation("ChannelConverterNode: {Direction}",
            monoToStereo ? "Mono -> Stereo" : "Stereo -> Mono");
    }
    
    public async Task StartAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var frame in _input.ReadAllAsync(ct))
            {
                var inSamples = frame.Samples.Span;
                
                if (_monoToStereo)
                {
                    // Mono -> Stereo: duplicate each sample
                    var outLength = inSamples.Length * 2;
                    var outBuffer = ArrayPool<float>.Shared.Rent(outLength);
                    
                    try
                    {
                        for (int i = 0; i < inSamples.Length; i++)
                        {
                            outBuffer[i * 2] = inSamples[i];     // Left
                            outBuffer[i * 2 + 1] = inSamples[i]; // Right
                        }
                        
                        var convertedFrame = frame with { Samples = outBuffer.AsMemory(0, outLength) };
                        
                        await _output.Writer.WriteAsync(convertedFrame, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error converting mono to stereo");
                        ArrayPool<float>.Shared.Return(outBuffer);
                        throw;
                    }
                }
                else
                {
                    // Stereo -> Mono: average both channels
                    if (inSamples.Length % 2 != 0)
                    {
                        _logger.LogWarning("Expected stereo audio (even sample count), got {Count} samples", inSamples.Length);
                        // Pass through as-is if already mono
                        await _output.Writer.WriteAsync(frame, ct);
                        continue;
                    }
                    
                    var outLength = inSamples.Length / 2;
                    var outBuffer = ArrayPool<float>.Shared.Rent(outLength);
                    
                    try
                    {
                        for (int i = 0; i < outLength; i++)
                        {
                            outBuffer[i] = (inSamples[i * 2] + inSamples[i * 2 + 1]) / 2f;
                        }
                        
                        var convertedFrame = frame with { Samples = outBuffer.AsMemory(0, outLength) };
                        
                        await _output.Writer.WriteAsync(convertedFrame, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error converting stereo to mono");
                        ArrayPool<float>.Shared.Return(outBuffer);
                        throw;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        finally
        {
            _output.Writer.Complete();
        }
    }
}
