using System.Buffers;
using System.Threading.Channels;
using BrainService.Domain.Audio;
using NAudio.Dsp;

namespace BrainService.Services.Audio.Nodes;

public class ResamplerNode : IAudioNode
{
    private readonly Channel<AudioFrame> _output;
    private readonly ChannelReader<AudioFrame> _input;
    private readonly WdlResampler _resampler;
    private readonly int _fromRate;
    private readonly int _toRate;
    private readonly ILogger<ResamplerNode> _logger;
    
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
        
        _resampler = new WdlResampler();
        _resampler.SetRates(fromRate, toRate);
        
        _output = Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(4)
        {
            FullMode = BoundedChannelFullMode.Wait
        });
        
        _logger.LogInformation("ResamplerNode: {FromRate}Hz -> {ToRate}Hz",
            fromRate, toRate);
    }
    
    public async Task StartAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var frame in _input.ReadAllAsync(ct))
            {
                var inSamples = frame.Samples.ToArray();
                
                // Calculate output size based on ratio
                var ratio = (double)_toRate / _fromRate;
                var outLength = (int)(inSamples.Length * ratio);
                var outBuffer = ArrayPool<float>.Shared.Rent(outLength + 64); // Extra space for safety
                
                try
                {
                    var inputCount = _resampler.ResamplePrepare(inSamples.Length, 1, out var inBuffer, out var inBufferOffset);
                    Array.Copy(inSamples, 0, inBuffer, inBufferOffset, inputCount);
                    var outSamples = _resampler.ResampleOut(outBuffer, 0, inputCount, outLength, 1);
                    
                    var resampledFrame = frame with { Samples = outBuffer.AsMemory(0, outSamples) };
                    
                    await _output.Writer.WriteAsync(resampledFrame, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error resampling frame");
                    ArrayPool<float>.Shared.Return(outBuffer);
                    throw;
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
