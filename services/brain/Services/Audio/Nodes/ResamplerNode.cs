using System.Buffers;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using BrainService.Domain.Audio;
using NAudio.Dsp;

namespace BrainService.Services.Audio.Nodes;

public class ResamplerNode : IAudioNode
{
    private readonly Channel<AudioFrame> _output;
    private readonly ChannelReader<AudioFrame> _input;
    private readonly int _fromRate;
    private readonly int _toRate;
    private readonly ILogger<ResamplerNode> _logger;
    
    private readonly Dictionary<ulong, WdlResampler> _resamplers = new();
    
    public ChannelReader<AudioFrame> Output => _output.Reader;
    
    public ResamplerNode(
        ChannelReader<AudioFrame> input,
        int fromRate,
        int toRate,
        ILogger<ResamplerNode> logger)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _fromRate = fromRate;
        _toRate = toRate;
        _logger = logger;
        
        _output = Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(4)
        {
            FullMode = BoundedChannelFullMode.Wait
        });
        
        _logger.LogInformation("ResamplerNode: {FromRate}Hz -> {ToRate}Hz", fromRate, toRate);
    }
    
    public async Task StartAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var frame in _input.ReadAllAsync(ct))
            {
                float[]? outBuffer = null;

                try
                {
                    if (!_resamplers.TryGetValue(frame.UserId, out var resampler))
                    {
                        resampler = new WdlResampler();
                        resampler.SetFeedMode(true);
                        resampler.SetRates(_fromRate, _toRate);
                        _resamplers[frame.UserId] = resampler;
                        _logger.LogDebug("Created resampler for user {UserId}", frame.UserId);
                    }

                    var inSpan = frame.Samples.Span;
                    var ratio = (double)_toRate / _fromRate;
                    var estimatedOutLength = (int)(inSpan.Length * ratio) + 64;
                    outBuffer = ArrayPool<float>.Shared.Rent(estimatedOutLength);

                    var inputCount =
                        resampler.ResamplePrepare(inSpan.Length, 1, out var inBuffer, out var inBufferOffset);
                    inSpan[..inputCount].CopyTo(inBuffer.AsSpan(inBufferOffset));

                    // Return the incoming buffer now that we've copied the data out
                    if (MemoryMarshal.TryGetArray(frame.Samples, out var seg) && seg.Array != null)
                        ArrayPool<float>.Shared.Return(seg.Array);

                    var outSamples = resampler.ResampleOut(outBuffer, 0, inputCount, outBuffer.Length, 1);

                    if (outSamples > 0)
                    {
                        await _output.Writer.WriteAsync(
                            frame with { Samples = outBuffer.AsMemory(0, outSamples) }, ct);
                        outBuffer = null; // Ownership transferred downstream
                    }
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
}
