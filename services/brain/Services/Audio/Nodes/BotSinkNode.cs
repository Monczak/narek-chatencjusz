using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using BrainService.Domain.Audio;
using Grpc.Core;
using Google.Protobuf;
using AudioFrame = BrainService.Domain.Audio.AudioFrame;

namespace BrainService.Services.Audio.Nodes;

public class BotSinkNode(
    ChannelReader<AudioFrame> input,
    IServerStreamWriter<Proto.AudioFrame> grpcOutput,
    ulong guildId,
    ILogger<BotSinkNode> logger)
    : IAudioNode
{
    private readonly ChannelReader<AudioFrame> _input = input ?? throw new ArgumentNullException(nameof(input));
    private readonly IServerStreamWriter<Proto.AudioFrame> _grpcOutput = grpcOutput ?? throw new ArgumentNullException(nameof(grpcOutput));

    public ChannelReader<AudioFrame> Output => throw new NotSupportedException("Sink has no output");
    
    public double AverageLatencyMs { get; private set; }
    
    // 48kHz stereo, 20ms frames
    private const int FrameSize = 960 * 2; // 1920 samples

    public async Task StartAsync(CancellationToken ct)
    {
        logger.LogInformation("BotSinkNode started for guild {GuildId}", guildId);
        
        // RING BUFFER STRATEGY
        // We allocate 50 buffers (1 second of audio). 
        // This gives gRPC 1000ms to send a frame before we dare to touch that memory again.
        // This creates ZERO allocations per frame after the initial setup.
        const int bufferCount = 50; 
        var ringBuffer = new byte[bufferCount][];
        for (int i = 0; i < bufferCount; i++)
        {
            ringBuffer[i] = new byte[FrameSize * 2];
        }
        
        long frameIndex = 0;
        const double smoothingFactor = 0.1;
        const int writeTimeoutMs = 40; 

        var reuseCts = new CancellationTokenSource();
        var reusableGrpcFrame = new Proto.AudioFrame 
        { 
            GuildId = guildId 
        };
        
        try
        {
            while (await _input.WaitToReadAsync(ct))
            {
                if (!_input.TryRead(out var frame)) continue;

                var currentLatency = (DateTime.UtcNow - frame.Timestamp).TotalMilliseconds;
                AverageLatencyMs = AverageLatencyMs == 0
                    ? currentLatency
                    : AverageLatencyMs * (1 - smoothingFactor) + currentLatency * smoothingFactor;

                // 1. Select the next buffer in the ring
                var currentBuffer = ringBuffer[frameIndex % bufferCount];
                frameIndex++;

                // 2. Write PCM data directly into this specific buffer
                ConvertFloatToPcm(frame.Samples.Span, currentBuffer.AsSpan());

                // 3. Return the float[] to the pool (standard logic)
                if (MemoryMarshal.TryGetArray(frame.Samples, out var segment) && segment.Array != null)
                    ArrayPool<float>.Shared.Return(segment.Array);

                // 4. ZERO-COPY WRAP
                // It is now safe to wrap this buffer because we won't touch 'currentBuffer' 
                // again for another 50 iterations (1 second).
                var pcmData = UnsafeByteOperations.UnsafeWrap(currentBuffer);

                reusableGrpcFrame.PcmData = pcmData;

                // EFFICIENT TIMEOUT PATTERN
                // 1. Reset the reusable token
                if (!reuseCts.TryReset())
                {
                    // Should practically never happen unless cancelled previously
                    reuseCts.Dispose(); 
                    reuseCts = new CancellationTokenSource();
                }
            
                // 2. Set the timeout
                reuseCts.CancelAfter(writeTimeoutMs);
            
                // 3. Link with the main token (without allocating a LinkedTokenSource)
                // We pass the reusable token to WriteAsync. 
                // Note: If 'ct' (main token) cancels, the loop exits anyway, 
                // so we don't strictly need to link them for the Write call itself 
                // as long as we check 'ct' in the loop.
            
                try
                {
                    await _grpcOutput.WriteAsync(reusableGrpcFrame, reuseCts.Token);
                }
                catch (OperationCanceledException)
                {
                    if (ct.IsCancellationRequested) throw; // Main token cancelled
                
                    // Otherwise it was our timeout
                    logger.LogWarning("BotSinkNode: frame dropped (write timeout)");
                }
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("BotSinkNode cancelled for guild {GuildId}", guildId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "BotSinkNode error for guild {GuildId}", guildId);
            throw;
        }
        finally
        {
            // No ArrayPool to return to, we own the ringBuffer. GC will handle it.
            reuseCts.Dispose();
            logger.LogInformation("BotSinkNode stopped for guild {GuildId}", guildId);
        }
    }
    
    private static void ConvertFloatToPcm(ReadOnlySpan<float> floatSamples, Span<byte> pcmBytes)
    {
        var sampleCount = Math.Min(floatSamples.Length, pcmBytes.Length / 2);
        
        for (int i = 0; i < sampleCount; i++)
        {
            var sample = Math.Clamp(floatSamples[i], -1f, 1f);
            var intSample = (short)(sample * 32767f);
            BinaryPrimitives.WriteInt16LittleEndian(pcmBytes.Slice(i * 2, 2), intSample);
        }
    }
}
