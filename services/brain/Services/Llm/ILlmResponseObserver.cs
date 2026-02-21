namespace BrainService.Services.Llm;

public interface ILlmResponseObserver
{
    Task OnResponseStarted(string sessionId, ulong guildId, CancellationToken ct);

    void OnTextDelta(string sessionId, string delta);
    
    Task<bool> OnResponseCompletedAsync(string sessionId, CancellationToken ct);

    string? OnResponseCanceled(string sessionId);
}