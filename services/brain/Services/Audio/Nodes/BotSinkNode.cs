using System.Buffers;
using System.Buffers.Binary;
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
        
        var pcmBuffer = ArrayPool<byte>.Shared.Rent(FrameSize * 2); // 2 bytes per sample

        const double smoothingFactor = 0.1;
        
        try
        {
            // Pull frame from upstream (mixer)
            while (await _input.WaitToReadAsync(ct))
            {
                if (_input.TryRead(out var frame))
                {
                    var currentLatency = (DateTime.UtcNow - frame.Timestamp).TotalMilliseconds;
                    if (AverageLatencyMs == 0)
                    {
                        AverageLatencyMs = currentLatency;
                    }
                    else
                    {
                        AverageLatencyMs = AverageLatencyMs * (1 - smoothingFactor) +
                                           currentLatency * smoothingFactor;
                    }
                    // Convert float32 to int16 PCM
                    ConvertFloatToPcm(frame.Samples.Span, pcmBuffer.AsSpan(0, FrameSize * 2));
                    
                    var grpcFrame = new Proto.AudioFrame
                    {
                        GuildId = guildId,
                        PcmData = ByteString.CopyFrom(pcmBuffer, 0, FrameSize * 2)
                    };
                    
                    await _grpcOutput.WriteAsync(grpcFrame, ct);
                    
                    if (MemoryMarshal.TryGetArray(frame.Samples, out var segment) && segment.Array != null)
                    {
                        ArrayPool<float>.Shared.Return(segment.Array);
                    }
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
            ArrayPool<byte>.Shared.Return(pcmBuffer);
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
