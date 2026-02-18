namespace BrainService.Domain.Llm;

public record LlmMessage(string Role, string Content);

public record LlmToolDefinition(string Name, string Description, object JsonSchema);

public record LlmGenerationSettings(
    string? ModelName = null,
    float Temperature = 1.0f,
    int MaxTokens = 1024,
    float? TopP = null,
    float? RepetitionPenalty = null
);

public record LlmRequest(
    IReadOnlyList<LlmMessage> Messages,
    LlmGenerationSettings Settings,
    IReadOnlyList<LlmToolDefinition>? Tools = null
);

public record LlmToolCall(string Id, string Name, string ArgumentsJson);

public enum LlmFinishReason
{
    Stop,
    Cancelled,
    Length,
    ToolCalls,
    Error
}

public record LlmStreamChunk(
    string? TextDelta,
    LlmToolCall? ToolCall,
    bool IsComplete,
    LlmFinishReason? FinishReason
);

public enum LlmContextReason
{
    UserSilence,
    Ramble,
    UserJoined,
    UserLeft,
}
