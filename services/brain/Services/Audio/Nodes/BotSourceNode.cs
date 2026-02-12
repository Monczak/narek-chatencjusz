using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using BrainService.Domain.Audio;
using BrainService.Proto;
using Grpc.Core;
using AudioFrame = BrainService.Domain.Audio.AudioFrame;

namespace BrainService.Services.Audio.Nodes;

public class BotSourceNode(
    IAsyncStreamReader<UserAudioFrame> grpcInput,
    string sessionId,
    ILogger<BotSourceNode> logger)
    : IAudioNode
{
    private readonly Channel<AudioFrame> _output = Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(16)
    {
        FullMode = BoundedChannelFullMode.Wait
    });
    private readonly IAsyncStreamReader<UserAudioFrame> _grpcInput = grpcInput ?? throw new ArgumentNullException(nameof(grpcInput));

    public ChannelReader<AudioFrame> Output => _output.Reader;
    
    private const int SamplesPerFrame = 1920; // 48kHz * 20ms * 2 channels
    
    private const int PreBufferThreshold = 6; // Wait for 6 frames (120ms) before playing
    private const int ResetThresholdMs = 200; // If no data for 200ms, assume silence and re-buffer

    public async Task StartAsync(CancellationToken ct)
    {
        logger.LogInformation("BotSourceNode started for session {SessionId}", sessionId);
        
        var jitterQueue = new Queue<AudioFrame>();
        var isBuffering = true;
        var lastFrameReceiveTime = DateTime.UtcNow;
        
        try
        {
            await foreach (var grpcFrame in _grpcInput.ReadAllAsync(ct))
            {
                var now = DateTime.UtcNow;
                var timeSinceLastFrame = (now - lastFrameReceiveTime).TotalMilliseconds;
                lastFrameReceiveTime = now;

                // 1. Underrun/Silence Detection
                if (!isBuffering && timeSinceLastFrame > ResetThresholdMs)
                {
                    logger.LogDebug("Stream gap of {Gap}ms detected. Re-buffering...", (int)timeSinceLastFrame);
                    isBuffering = true;
                }
                
                // Validate frame
                if (grpcFrame.PcmData.Length != SamplesPerFrame * 2) // 2 bytes per sample
                {
                    logger.LogWarning("Invalid frame size: {Size}", grpcFrame.PcmData.Length);
                    continue;
                }
                
                // Convert PCM int16 to float32
                var floatSamples = ArrayPool<float>.Shared.Rent(SamplesPerFrame);
                ConvertPcmToFloat(grpcFrame.PcmData.Span, floatSamples.AsSpan(0, SamplesPerFrame));
                
                var audioFrame = new AudioFrame
                {
                    Samples = floatSamples.AsMemory(0, SamplesPerFrame),
                    UserId = grpcFrame.UserId,
                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(grpcFrame.Timestamp).UtcDateTime
                };
                
                if (isBuffering)
                {
                    jitterQueue.Enqueue(audioFrame);
                    
                    if (jitterQueue.Count >= PreBufferThreshold)
                    {
                        logger.LogDebug("Jitter buffer full ({Count} frames). Flushing.", jitterQueue.Count);
                        while (jitterQueue.TryDequeue(out var queuedFrame))
                        {
                            await _output.Writer.WriteAsync(queuedFrame, ct);
                        }
                        isBuffering = false;
                    }
                }
                else
                {
                    // Fast path
                    await _output.Writer.WriteAsync(audioFrame, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("BotSourceNode cancelled for session {SessionId}", sessionId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "BotSourceNode error for session {SessionId}", sessionId);
            throw;
        }
        finally
        {
            // Cleanup leftover frames
            while (jitterQueue.TryDequeue(out var frame))
            {
                if (MemoryMarshal.TryGetArray(frame.Samples, out var segment) && segment.Array != null)
                {
                    ArrayPool<float>.Shared.Return(segment.Array);
                }
            }
            
            _output.Writer.Complete();
            logger.LogInformation("BotSourceNode stopped for session {SessionId}", sessionId);
        }
    }
    
    private static void ConvertPcmToFloat(ReadOnlySpan<byte> pcmBytes, Span<float> floatSamples)
    {
        for (var i = 0; i < floatSamples.Length; i++)
        {
            var pcmSample = BinaryPrimitives.ReadInt16LittleEndian(pcmBytes.Slice(i * 2, 2));
            floatSamples[i] = pcmSample / 32768f;
        }
    }
}
