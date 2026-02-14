using System.Runtime.InteropServices;
using BrainService.Domain.Asr;
using BrainService.Proto.Asr;
using BrainService.Proto.Brain;
using BrainService.Services.Audio.Nodes;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;

namespace BrainService.Services.Asr;

public class AsrGrpcClient : IDisposable
{
    private readonly Proto.Asr.Asr.AsrClient _client;
    private readonly GrpcChannel _channel;
    private readonly ILogger<AsrGrpcClient> _logger;

    public AsrGrpcClient(string url, ILogger<AsrGrpcClient> logger)
    {
        _logger = logger;
        _channel = GrpcChannel.ForAddress($"http://{url}");
        _client = new Proto.Asr.Asr.AsrClient(_channel);
    }

    public async Task<TranscriptResult?> TranscribeAsync(
        string sessionId,
        ulong userId,
        float[] buffer,
        int sampleCount,
        DateTime startedAt,
        DateTime endedAt,
        CancellationToken ct = default
    )
    {
        try
        {
            var pcmBytes = MemoryMarshal.AsBytes(buffer.AsSpan(0, sampleCount)).ToArray();

            var request = new UtteranceRequest
            {
                SessionId = sessionId,
                UserId = userId,
                PcmF32Mono16K = ByteString.CopyFrom(pcmBytes),
                StartedAtMs = new DateTimeOffset(startedAt, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                EndedAtMs = new DateTimeOffset(endedAt, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            };

            var response = await _client.TranscribeAsync(
                request,
                cancellationToken: ct,
                deadline: DateTime.UtcNow.AddSeconds(30)
            );

            if (string.IsNullOrEmpty(response.Text))
            {
                return null;
            }

            return new TranscriptResult(
                SessionId: sessionId,
                UserId: userId,
                Text: response.Text,
                Confidence: response.Confidence,
                Language: response.Language,
                StartedAt: DateTimeOffset.FromUnixTimeMilliseconds(response.StartedAtMs).UtcDateTime,
                EndedAt: DateTimeOffset.FromUnixTimeMilliseconds(response.EndedAtMs).UtcDateTime
            );
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.DeadlineExceeded)
        {
            _logger.LogWarning("ASR transcription timed out for session {Session}", sessionId);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ASR transcription failed for session {Session}", sessionId);
            return null;
        }
    }

    public void Dispose()
    {
        _channel.Dispose();
    }
}