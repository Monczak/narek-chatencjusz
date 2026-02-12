using System.Buffers;
using System.Runtime.InteropServices;
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
                float[]? outBuffer = null;

                try
                {
                    int outLength;
                    if (_monoToStereo)
                    {
                        outLength = inSamples.Length * 2;
                        outBuffer = ArrayPool<float>.Shared.Rent(outLength);

                        for (var i = 0; i < inSamples.Length; i++)
                        {
                            outBuffer[i * 2] = inSamples[i]; // L
                            outBuffer[i * 2 + 1] = inSamples[i]; // R
                        }
                    }
                    else
                    {
                        if (inSamples.Length % 2 != 0)
                        {
                            _logger.LogWarning(
                                "Stereo->Mono: expected even sample count, got {Count}", inSamples.Length);
                            // Pass through as-is; don't touch the buffer ownership
                            await _output.Writer.WriteAsync(frame, ct);
                            continue;
                        }

                        outLength = inSamples.Length / 2;
                        outBuffer = ArrayPool<float>.Shared.Rent(outLength);

                        for (var i = 0; i < outLength; i++)
                        {
                            outBuffer[i] = (inSamples[i * 2] + inSamples[i * 2 + 1]) * 0.5f;
                        }
                    }
                    
                    ReturnFrameBuffer(frame);

                    await _output.Writer.WriteAsync(
                        frame with { Samples = outBuffer.AsMemory(0, outLength) }, ct);
                    outBuffer = null; // Ownership transferred downstream
                }
                finally
                {
                    if (outBuffer != null)
                        ArrayPool<float>.Shared.Return(outBuffer);
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
    
    private static void ReturnFrameBuffer(AudioFrame frame)
    {
        if (MemoryMarshal.TryGetArray(frame.Samples, out var seg) && seg.Array != null)
            ArrayPool<float>.Shared.Return(seg.Array);
    }
}
