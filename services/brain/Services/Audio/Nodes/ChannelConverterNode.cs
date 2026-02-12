using System.Threading.Channels;
using BrainService.Domain.Audio;

namespace BrainService.Services.Audio.Nodes;

public class ChannelConverterNode(
    ChannelReader<AudioFrame> input,
    bool monoToStereo,
    ILogger<ChannelConverterNode> logger)
    : AudioFilterNode(input, logger)
{
    protected override ValueTask<AudioFrame?> ProcessFrameAsync(AudioFrame frame, CancellationToken ct)
    {
        var inSamples = frame.Samples.Span;
        
        if (!monoToStereo && inSamples.Length % 2 != 0)
        {
            Logger.LogWarning("Stereo->Mono: expected even sample count, got {Count}", inSamples.Length);
            return new ValueTask<AudioFrame?>(frame);
        }

        int outLength;
        float[] outBuffer;

        if (monoToStereo)
        {
            outLength = inSamples.Length * 2;
            outBuffer = RentBuffer(outLength);
            for (var i = 0; i < inSamples.Length; i++)
            {
                outBuffer[i * 2] = inSamples[i];     // L
                outBuffer[i * 2 + 1] = inSamples[i]; // R
            }
        }
        else // Stereo -> Mono
        {
            outLength = inSamples.Length / 2;
            outBuffer = RentBuffer(outLength);
            for (var i = 0; i < outLength; i++)
            {
                outBuffer[i] = (inSamples[i * 2] + inSamples[i * 2 + 1]) * 0.5f;
            }
        }
        
        return new ValueTask<AudioFrame?>(frame with 
        { 
            Samples = outBuffer.AsMemory(0, outLength) 
        });
    }
}
