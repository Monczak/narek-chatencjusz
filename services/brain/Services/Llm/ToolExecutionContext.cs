using BrainService.Domain.Session;

namespace BrainService.Services.Llm;

public class ToolExecutionContext
{
    public required string SessionId { get; init; }
    public required ulong GuildId { get; init; }
    public required VoiceSessionState SessionState { get; init; }
}