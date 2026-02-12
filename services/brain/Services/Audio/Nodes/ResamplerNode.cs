using System.Threading.Channels;
using BrainService.Domain.Audio;
using NAudio.Dsp;

namespace BrainService.Services.Audio.Nodes;

public class ResamplerNode(
    ChannelReader<AudioFrame> input,
    int fromRate,
    int toRate,
    ILogger<ResamplerNode> logger)
    : AudioFilterNode(input, logger)
{
    private readonly Dictionary<ulong, WdlResampler> _resamplers = new();

    protected override ValueTask<AudioFrame?> ProcessFrameAsync(AudioFrame frame, CancellationToken ct)
    {
        if (!_resamplers.TryGetValue(frame.UserId, out var resampler))
        {
            resampler = new WdlResampler();
            resampler.SetFeedMode(true);
            resampler.SetRates(fromRate, toRate);
            _resamplers[frame.UserId] = resampler;
        }

        var inSpan = frame.Samples.Span;
        
        var inputCount = resampler.ResamplePrepare(inSpan.Length, 1, out var inBuffer, out var inBufferOffset);
        inSpan[..inputCount].CopyTo(inBuffer.AsSpan(inBufferOffset));
        
        var ratio = (double)toRate / fromRate;
        var estimatedOutLength = (int)(inSpan.Length * ratio) + 64;
        
        var outBuffer = RentBuffer(estimatedOutLength);
        
        var outSamples = resampler.ResampleOut(outBuffer, 0, inputCount, outBuffer.Length, 1);

        if (outSamples > 0)
        {
            return new ValueTask<AudioFrame?>(frame with 
            { 
                Samples = outBuffer.AsMemory(0, outSamples) 
            });
        }
        
        ReturnRawBuffer(outBuffer);
        return new ValueTask<AudioFrame?>(result: null);
    }
}
