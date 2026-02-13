using System.Buffers;
using System.Threading.Channels;
using BrainService.Domain.Audio;

namespace BrainService.Services.Audio.Nodes;

public sealed class SineWaveSourceNode : IAudioNode
{
    private readonly Channel<AudioFrame> _output = Channel.CreateBounded<AudioFrame>(
        new BoundedChannelOptions(50) { FullMode = BoundedChannelFullMode.DropOldest });

    public ChannelReader<AudioFrame> Output => _output.Reader;

    private const int SampleRate = 48000;
    private const int SamplesPerFrame = 1920; // 960 stereo pairs for 20 ms
    private const double Frequency = 440.0;
    private const float Volume = 0.2f; // Reduced volume to prevent ear strain

    public async Task StartAsync(CancellationToken ct)
    {
        double phase = 0.0;
        double phaseIncrement = 2 * Math.PI * Frequency / SampleRate;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));

        while (await timer.WaitForNextTickAsync(ct))
        {
            var floats = ArrayPool<float>.Shared.Rent(SamplesPerFrame);
            
            for (int i = 0; i < SamplesPerFrame; i += 2)
            {
                float sample = (float)Math.Sin(phase) * Volume;
                floats[i] = sample;     // Left channel
                floats[i + 1] = sample; // Right channel
                
                phase += phaseIncrement;
            }
            if (phase >= 2 * Math.PI) phase -= 2 * Math.PI;

            var frame = new AudioFrame
            {
                Samples = floats.AsMemory(0, SamplesPerFrame),
                Timestamp = DateTime.UtcNow
            };

            if (!_output.Writer.TryWrite(frame))
            {
                ArrayPool<float>.Shared.Return(floats);
            }
        }
    }
}