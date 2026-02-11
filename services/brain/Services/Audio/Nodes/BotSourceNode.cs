using System.Buffers;
using System.Buffers.Binary;
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
    private readonly Channel<AudioFrame> _output = Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(4)
    {
        FullMode = BoundedChannelFullMode.Wait
    });
    private readonly IAsyncStreamReader<UserAudioFrame> _grpcInput = grpcInput ?? throw new ArgumentNullException(nameof(grpcInput));

    public ChannelReader<AudioFrame> Output => _output.Reader;
    
    // Audio format constants - bot sends 48kHz 16-bit stereo PCM
    private const int SampleRate = 48000;
    private const int Channels = 2; // Stereo
    private const int BytesPerSample = 2; // int16
    private const int FrameDurationMs = 20;
    private const int SamplesPerFrame = (SampleRate * FrameDurationMs / 1000) * Channels; // 1920 samples (960 per channel)

    public async Task StartAsync(CancellationToken ct)
    {
        logger.LogInformation("BotSourceNode started for session {SessionId}", sessionId);
        
        try
        {
            await foreach (var grpcFrame in _grpcInput.ReadAllAsync(ct))
            {
                // Validate frame
                if (grpcFrame.PcmData.Length != SamplesPerFrame * BytesPerSample)
                {
                    logger.LogWarning("Received frame with unexpected size: {Size} bytes (expected {Expected})",
                        grpcFrame.PcmData.Length, SamplesPerFrame * BytesPerSample);
                    continue;
                }
                
                // Convert PCM int16 to float32 [-1.0, 1.0]
                var floatSamples = ArrayPool<float>.Shared.Rent(SamplesPerFrame);
                try
                {
                    ConvertPcmToFloat(grpcFrame.PcmData.Span, floatSamples.AsSpan(0, SamplesPerFrame));
                    
                    var audioFrame = new AudioFrame
                    {
                        Samples = floatSamples.AsMemory(0, SamplesPerFrame),
                        UserId = grpcFrame.UserId,
                        Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(grpcFrame.Timestamp).UtcDateTime,
                        SessionId = sessionId
                    };
                    
                    await _output.Writer.WriteAsync(audioFrame, ct);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error processing frame from user {UserId}", grpcFrame.UserId);
                    ArrayPool<float>.Shared.Return(floatSamples);
                    throw;
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
            _output.Writer.Complete();
            logger.LogInformation("BotSourceNode stopped for session {SessionId}", sessionId);
        }
    }
    
    private static void ConvertPcmToFloat(ReadOnlySpan<byte> pcmBytes, Span<float> floatSamples)
    {
        for (int i = 0; i < floatSamples.Length; i++)
        {
            var pcmSample = BinaryPrimitives.ReadInt16LittleEndian(pcmBytes.Slice(i * 2, 2));
            floatSamples[i] = pcmSample / 32768f;
        }
    }
}
