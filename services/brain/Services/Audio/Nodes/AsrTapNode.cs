using System.Buffers;
using System.Threading.Channels;
using BrainService.Domain.Audio;
using BrainService.Services.Asr;
using BrainService.Services.Session;

namespace BrainService.Services.Audio.Nodes;

public sealed class AsrTapNode(
    ChannelReader<AudioFrame> input,
    AsrGrpcClient asrClient,
    VoiceSessionHistoryService history,
    string sessionId,
    ulong userId,
    ILogger<AsrTapNode> logger)
    : IAudioNode
{
    private const int SampleRate = 16_000;
    private const int MaxSeconds = 60;
    
    private const int RingCapacity = SampleRate * MaxSeconds;
    
    private readonly Channel<AudioFrame> _output = Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(8)
    {
        FullMode     = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = true,
    });
    
    
    private readonly float[] _ringBuffer = new float[RingCapacity];
    private int _ringBufferIndex;
    private int _storedSamples;
    private DateTime _utteranceStartedAt;
    
    private CancellationTokenSource _flushCts = new();
    
    public ChannelReader<AudioFrame> Output => _output.Reader;
    public int QueueDepth => _output.Reader.Count;

    public void OnSpeakingStateChanged(bool isSpeaking)
    {
        if (isSpeaking)
        {
            _utteranceStartedAt = DateTime.UtcNow;
        }
        else
        {
            Volatile.Read(ref _flushCts).Cancel();
        }
    }

    public async Task StartAsync(CancellationToken ct)
    {
        logger.LogInformation("AsrTapNode started");
        
        try
        {
            while (true)
            {
                bool hasData;
                try
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _flushCts.Token);
                    hasData = await input.WaitToReadAsync(linked.Token);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (OperationCanceledException)
                {
                    while (input.TryRead(out var extra))
                    {
                        Accumulate(extra.Samples.Span);
                        await _output.Writer.WriteAsync(extra, ct);
                    }
                    
                    var oldCts = Interlocked.Exchange(ref _flushCts, new CancellationTokenSource());
                    oldCts.Dispose();

                    DispatchUtterance();
                    continue;
                }

                if (!hasData) break;

                while (input.TryRead(out var frame))
                {
                    Accumulate(frame.Samples.Span);
                    await _output.Writer.WriteAsync(frame, ct);
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
            _flushCts.Dispose();
            logger.LogInformation("AsrTapNode stopped");
        }
    }
    
    private void Accumulate(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return;

        var tailSpace = RingCapacity - _ringBufferIndex;

        if (samples.Length <= tailSpace)
        {
            samples.CopyTo(_ringBuffer.AsSpan(_ringBufferIndex));
            _ringBufferIndex += samples.Length;
            if (_ringBufferIndex >= RingCapacity)
            {
                _ringBufferIndex = 0;
            }
        }
        else
        {
            samples[..tailSpace].CopyTo(_ringBuffer.AsSpan(_ringBufferIndex));
            samples[tailSpace..].CopyTo(_ringBuffer.AsSpan());
            _ringBufferIndex = samples.Length - tailSpace;
        }

        _storedSamples = Math.Min(_storedSamples + samples.Length, RingCapacity);
    }

    private void DispatchUtterance()
    {
        if (_storedSamples == 0) return;

        var count = _storedSamples;
        var startedAt = _utteranceStartedAt == default ? DateTime.UtcNow : _utteranceStartedAt;
        var endedAt = DateTime.UtcNow;

        var buffer = ArrayPool<float>.Shared.Rent(count);
        var cursor = (_ringBufferIndex - count + RingCapacity) % RingCapacity;

        if (cursor + count <= RingCapacity)
        {
            _ringBuffer.AsSpan(cursor, count).CopyTo(buffer);
        }
        else
        {
            var firstPart = RingCapacity - cursor;
            _ringBuffer.AsSpan(cursor, firstPart).CopyTo(buffer);
            _ringBuffer.AsSpan(0, count - firstPart).CopyTo(buffer.AsSpan(firstPart));
        }

        _storedSamples = 0;

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await asrClient.TranscribeAsync(sessionId, userId, buffer, count, startedAt, endedAt);

                if (result is not null)
                {
                    await history.AppendTranscriptAsync(result);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "ASR dispatch failed for session {Session} user {User}", sessionId, userId);
            }
            finally
            {
                ArrayPool<float>.Shared.Return(buffer);
            }
        });
    }
}