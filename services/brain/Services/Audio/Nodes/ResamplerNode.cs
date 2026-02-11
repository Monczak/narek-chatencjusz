using System.Buffers;
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
                // Retrieve or create the user's dedicated resampler state
                if (!_resamplers.TryGetValue(frame.UserId, out var resampler))
                {
                    resampler = new WdlResampler();
                    resampler.SetFeedMode(true);
                    resampler.SetRates(_fromRate, _toRate);
                    _resamplers[frame.UserId] = resampler;
                    _logger.LogDebug("Created dedicated resampler for user {UserId}", frame.UserId);
                }

                var inSamples = frame.Samples.ToArray();
                
                var ratio = (double)_toRate / _fromRate;
                var estimatedOutLength = (int)(inSamples.Length * ratio) + 64; 
                var outBuffer = ArrayPool<float>.Shared.Rent(estimatedOutLength); 
                
                try
                {
                    var inputCount = resampler.ResamplePrepare(inSamples.Length, 1, out var inBuffer, out var inBufferOffset);
                    Array.Copy(inSamples, 0, inBuffer, inBufferOffset, inputCount);
                    var outSamples = resampler.ResampleOut(outBuffer, 0, inputCount, outBuffer.Length, 1);
                    
                    var resampledFrame = frame with { Samples = outBuffer.AsMemory(0, outSamples) };
                    
                    await _output.Writer.WriteAsync(resampledFrame, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error resampling frame for user {UserId}", frame.UserId);
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
