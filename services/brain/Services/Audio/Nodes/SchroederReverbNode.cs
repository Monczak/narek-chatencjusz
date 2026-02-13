using System.Buffers;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using BrainService.Domain.Audio;

namespace BrainService.Services.Audio.Nodes;

public class SchroederReverbNode(ChannelReader<AudioFrame> input, ILoggerFactory loggerFactory)
    : IAudioNode
{
    private readonly Channel<AudioFrame> _output = Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(2)
    {
        FullMode = BoundedChannelFullMode.Wait
    });
    private readonly ILogger _logger = loggerFactory.CreateLogger<SchroederReverbNode>();
    
    // Reverb Parameters
    private const float WetMix = 0.35f; // 35% Reverb
    private const float DryMix = 0.8f;  
    
    // Filters
    private readonly CombFilter[] _combFilters =
    [
        new(delaySamples: 1426, feedback: 0.773f), // ~29.7ms
        new(delaySamples: 1781, feedback: 0.802f), // ~37.1ms
        new(delaySamples: 1973, feedback: 0.753f), // ~41.1ms
        new(delaySamples: 2098, feedback: 0.733f)  // ~43.7ms
    ];
    private readonly AllPassFilter[] _allPassFilters =
    [
        new(delaySamples: 240, feedback: 0.7f), // ~5ms
        new(delaySamples: 82,  feedback: 0.7f)  // ~1.7ms
    ];

    public ChannelReader<AudioFrame> Output => _output.Reader;
    public int QueueDepth => _output.Reader.Count;

    public async Task StartAsync(CancellationToken ct)
    {
        _logger.LogInformation("SchroederReverbNode started");
        
        try
        {
            while (await input.WaitToReadAsync(ct))
            {
                while (input.TryRead(out var frame))
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
        
        var outputBuffer = ArrayPool<float>.Shared.Rent(sampleCount);
        var outputSpan = outputBuffer.AsSpan(0, sampleCount);
        
        for (var i = 0; i < sampleCount; i += 2)
        {
            var inputL = inputSamples[i];
            var inputR = inputSamples[i + 1];
            
            var monoInput = (inputL + inputR) * 0.5f;
            
            var combSum = 0f;
            foreach (var comb in _combFilters)
            {
                combSum += comb.Process(monoInput);
            }
            
            var wetSignal = combSum;
            foreach (var apf in _allPassFilters)
            {
                wetSignal = apf.Process(wetSignal);
            }
            
            outputSpan[i] = inputL * DryMix + wetSignal * WetMix;
            outputSpan[i + 1] = inputR * DryMix + wetSignal * WetMix;
        }

        var outputFrame = inputFrame with { Samples = outputBuffer.AsMemory(0, sampleCount) };

        await _output.Writer.WriteAsync(outputFrame, ct);
        
        if (MemoryMarshal.TryGetArray(inputFrame.Samples, out var segment) && segment.Array != null)
        {
            ArrayPool<float>.Shared.Return(segment.Array);
        }
    }
    
    private class CombFilter(int delaySamples, float feedback)
    {
        private readonly float[] _buffer = new float[delaySamples];
        private int _index;

        public float Process(float input)
        {
            var delayed = _buffer[_index];
            var output = input + delayed * feedback;
            
            _buffer[_index] = output;
            _index = (_index + 1) % _buffer.Length;
            
            return delayed;
        }
    }

    private class AllPassFilter(int delaySamples, float feedback)
    {
        private readonly float[] _buffer = new float[delaySamples];
        private int _index;

        public float Process(float input)
        {
            var delayed = _buffer[_index];
            var output = -input + delayed;
            var feedbackOutput = input + output * feedback;
            
            _buffer[_index] = feedbackOutput;
            _index = (_index + 1) % _buffer.Length;
            
            return output;
        }
    }
}
