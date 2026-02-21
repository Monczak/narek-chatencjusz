namespace BrainService.Services.Llm;

public class NullLlmResponseObserver : ILlmResponseObserver
{
    public static readonly NullLlmResponseObserver Instance = new();
    
    public Task OnResponseStarted(string sessionId, ulong guildId, CancellationToken ct) => Task.CompletedTask;
    public void OnTextDelta(string sessionId, string delta) { }

    public Task<bool> OnResponseCompletedAsync(string sessionId, CancellationToken ct) => Task.FromResult(true);
    public string? OnResponseCanceled(string sessionId) => null;
}