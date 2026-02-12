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
    private readonly Channel<AudioFrame> _output = Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(50)
    {
        FullMode = BoundedChannelFullMode.DropOldest
    });
    private readonly IAsyncStreamReader<UserAudioFrame> _grpcInput = grpcInput ?? throw new ArgumentNullException(nameof(grpcInput));

    public ChannelReader<AudioFrame> Output => _output.Reader;
    
    private const int SamplesPerFrame = 1920; 

    public async Task StartAsync(CancellationToken ct)
    {
        logger.LogInformation("BotSourceNode started for session {SessionId}", sessionId);
        
        try
        {
            await foreach (var grpcFrame in _grpcInput.ReadAllAsync(ct))
            {
                // Validate frame
                if (grpcFrame.PcmData.Length != SamplesPerFrame * 2) 
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
                    Timestamp = DateTime.UtcNow
                };
                
                await _output.Writer.WriteAsync(audioFrame, ct);
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
