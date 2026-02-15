using BrainService.Domain.Llm;

namespace BrainService.Services.Llm;

public interface ILlmOrchestrationTrigger
{
    Task TriggerAsync(string sessionId, ulong guildId, LlmContextReason reason);
    void Cancel(string sessionId);
}