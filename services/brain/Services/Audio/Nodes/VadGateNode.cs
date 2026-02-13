using System.Buffers;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using BrainService.Domain.Audio;
using BrainService.Services.Audio.Vad;
using BrainService.Services.Configuration;

namespace BrainService.Services.Audio.Nodes;

public sealed class VadGateNode(
    ChannelReader<AudioFrame> input,
    SileroVadWrapper vad,
    BrainConfigService config,
    ILogger<VadGateNode> logger)
    : IAudioNode
{
    private readonly Channel<AudioFrame> _output = Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(8)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = true,
    });

    // State
    private readonly List<float> _sampleBuf = new(VadWindowSize * 4);
    private readonly Queue<AudioFrame> _preBuffer = new(16);
    private bool _isSpeaking;
    private DateTime _lastSpeakingFrame = DateTime.MinValue;

    private const int VadWindowSize = 512; // 16 kHz, 32 ms

    public ChannelReader<AudioFrame> Output => _output.Reader;

    public async Task StartAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var frame in input.ReadAllAsync(ct))
            {
                await ProcessFrameAsync(frame, ct);
            }
        }
        catch (OperationCanceledException)
        {
             // Normal shutdown
        }
        catch (Exception ex) { logger.LogError(ex, "VadGateNode error"); throw; }
        finally
        {
            while (_preBuffer.TryDequeue(out var f)) ReturnFrame(f);
            _output.Writer.Complete();
        }
    }

    private async ValueTask ProcessFrameAsync(AudioFrame frame, CancellationToken ct)
    {
        _sampleBuf.AddRange(frame.Samples.Span);
        var cfg = config.Current.Vad;

        while (_sampleBuf.Count >= VadWindowSize)
        {
            var chunk = CollectionsMarshal.AsSpan(_sampleBuf)[..VadWindowSize];
            var prob = vad.GetSpeechProbability(chunk);
            _sampleBuf.RemoveRange(0, VadWindowSize);

            if (!_isSpeaking && prob >= cfg.StartThreshold)
            {
                _isSpeaking = true;
                _lastSpeakingFrame = DateTime.UtcNow;

                // Flush pre-roll so speech doesn't start abruptly
                while (_preBuffer.TryDequeue(out var preFrame))
                    await _output.Writer.WriteAsync(preFrame, ct);
            }
            else if (_isSpeaking && prob > cfg.StopThreshold)
            {
                _lastSpeakingFrame = DateTime.UtcNow;
            }
        }
        
        if (_isSpeaking &&
            DateTime.UtcNow - _lastSpeakingFrame > TimeSpan.FromMilliseconds(cfg.SilenceDurationMs))
        {
            _isSpeaking = false;
        }

        if (_isSpeaking)
        {
            await _output.Writer.WriteAsync(frame, ct);
        }
        else
        {
            if (_preBuffer.Count >= cfg.PreBufferFrameCount)
                ReturnFrame(_preBuffer.Dequeue());
            _preBuffer.Enqueue(frame);
        }
    }

    private static void ReturnFrame(AudioFrame f)
    {
        if (MemoryMarshal.TryGetArray(f.Samples, out var seg) && seg.Array != null)
            ArrayPool<float>.Shared.Return(seg.Array);
    }
}
