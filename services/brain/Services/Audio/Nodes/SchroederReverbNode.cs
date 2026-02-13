using System.Buffers;
using System.Threading.Channels;
using BrainService.Domain.Audio;

namespace BrainService.Services.Audio.Nodes;

public class SchroederReverbNode : IAudioNode
{
    private readonly ChannelReader<AudioFrame> _input;
    private readonly Channel<AudioFrame> _output;
    private readonly ILogger _logger;

    // Audio settings
    private const int SampleRate = 48000;
    private const int Channels = 2;
    
    // Reverb Parameters
    private const float WetMix = 0.35f; // 35% Reverb
    private const float DryMix = 0.8f;  
    
    // Filters
    private readonly CombFilter[] _combFilters;
    private readonly AllPassFilter[] _allPassFilters;

    public ChannelReader<AudioFrame> Output => _output.Reader;
    public int QueueDepth => _output.Reader.Count;

    public SchroederReverbNode(ChannelReader<AudioFrame> input, ILoggerFactory loggerFactory)
    {
        _input = input;
        _logger = loggerFactory.CreateLogger<SchroederReverbNode>();
        
        _output = Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(2)
        {
            FullMode = BoundedChannelFullMode.Wait
        });

        // Initialize Schroeder Topology (Tuned for 48kHz)
        // 4 Parallel Comb Filters
        _combFilters = 
        [
            new CombFilter(delaySamples: 1426, feedback: 0.773f), // ~29.7ms
            new CombFilter(delaySamples: 1781, feedback: 0.802f), // ~37.1ms
            new CombFilter(delaySamples: 1973, feedback: 0.753f), // ~41.1ms
            new CombFilter(delaySamples: 2098, feedback: 0.733f)  // ~43.7ms
        ];

        // 2 Series All-Pass Filters
        _allPassFilters = 
        [
            new AllPassFilter(delaySamples: 240, feedback: 0.7f), // ~5ms
            new AllPassFilter(delaySamples: 82,  feedback: 0.7f)  // ~1.7ms
        ];
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _logger.LogInformation("SchroederReverbNode started");
        
        try
        {
            while (await _input.WaitToReadAsync(ct))
            {
                while (_input.TryRead(out var frame))
                {
                    await ProcessFrameAsync(frame, ct);
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
            _logger.LogInformation("SchroederReverbNode stopped");
        }
    }

    private async Task ProcessFrameAsync(AudioFrame inputFrame, CancellationToken ct)
    {
        var inputSamples = inputFrame.Samples.Span;
        var sampleCount = inputSamples.Length; // Stereo samples
        
        // Rent buffer for output
        var outputBuffer = ArrayPool<float>.Shared.Rent(sampleCount);
        var outputSpan = outputBuffer.AsSpan(0, sampleCount);

        // Process samples (Interleaved Stereo)
        for (int i = 0; i < sampleCount; i += 2)
        {
            float inputL = inputSamples[i];
            float inputR = inputSamples[i + 1];

            // 1. Create Mono Mix for Reverb Engine
            float monoInput = (inputL + inputR) * 0.5f;

            // 2. Process Parallel Comb Filters
            float combSum = 0f;
            foreach (var comb in _combFilters)
            {
                combSum += comb.Process(monoInput);
            }

            // 3. Process Series All-Pass Filters
            float wetSignal = combSum;
            foreach (var apf in _allPassFilters)
            {
                wetSignal = apf.Process(wetSignal);
            }

            // 4. Mix Wet + Dry and assign to stereo output
            // (We apply the same reverb tail to both channels here)
            outputSpan[i]     = (inputL * DryMix) + (wetSignal * WetMix);
            outputSpan[i + 1] = (inputR * DryMix) + (wetSignal * WetMix);
        }

        var outputFrame = new AudioFrame
        {
            Samples = outputBuffer.AsMemory(0, sampleCount),
            Timestamp = inputFrame.Timestamp,
            UserId = inputFrame.UserId
        };

        await _output.Writer.WriteAsync(outputFrame, ct);
        
        // Return *input* buffer to pool if it was rented upstream (standard pattern in your Mixer)
        // Note: In your current code, Mixer rents a buffer, passing it here. 
        // We just rented a NEW buffer for output. We must responsibly handle the memory.
        // Assuming upstream passes ownership:
        if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(inputFrame.Samples, out var segment) && segment.Array != null)
        {
            ArrayPool<float>.Shared.Return(segment.Array);
        }
    }
    
    // Inner DSP Classes
    
    private class CombFilter(int delaySamples, float feedback)
    {
        private readonly float[] _buffer = new float[delaySamples];
        private int _index = 0;

        public float Process(float input)
        {
            float delayed = _buffer[_index];
            float output = input + (delayed * feedback);
            
            _buffer[_index] = output;
            _index = (_index + 1) % _buffer.Length;
            
            return delayed;
        }
    }

    private class AllPassFilter(int delaySamples, float feedback)
    {
        private readonly float[] _buffer = new float[delaySamples];
        private int _index = 0;

        public float Process(float input)
        {
            float delayed = _buffer[_index];
            float output = -input + delayed;
            float feedbackOutput = input + (output * feedback);
            
            _buffer[_index] = feedbackOutput;
            _index = (_index + 1) % _buffer.Length;
            
            return output;
        }
    }
}
