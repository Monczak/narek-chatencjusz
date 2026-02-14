namespace BrainService.Domain.Asr;

public record TranscriptResult(
    string SessionId,
    ulong UserId,
    string Text,
    float Confidence,
    string Language,
    DateTime StartedAt,
    DateTime EndedAt
);