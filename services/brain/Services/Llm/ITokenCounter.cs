namespace BrainService.Services.Llm;

public interface ITokenCounter
{
    Task<int> CountTokensAsync(string text, CancellationToken ct = default);
}
