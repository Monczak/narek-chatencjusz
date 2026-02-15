namespace BrainService.Services.Llm;

public class FallbackTokenCounter : ITokenCounter
{
    public bool IsAvailable => false;

    public Task<int> CountTokensAsync(string text, CancellationToken ct = default)
    {
        return Task.FromResult(text.Length / 4 + 1); // Estimate (1 token = approx 4 letters for English)
    }
}