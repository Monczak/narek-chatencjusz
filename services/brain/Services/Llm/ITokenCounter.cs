using BrainService.Domain.Llm;

namespace BrainService.Services.Llm;

public interface ITokenCounter
{
    Task<int> CountTokensAsync(string text, CancellationToken ct = default);
    bool IsAvailable { get; }
    async Task<int> CountMessagesAsync(IEnumerable<LlmMessage> messages, CancellationToken ct = default)
    {
        var total = 0;
        foreach (var m in messages)
            total += await CountTokensAsync(m.Content, ct) + 4; // Role marker overhead
        return total;
    }
}