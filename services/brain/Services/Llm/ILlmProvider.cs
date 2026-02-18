using BrainService.Domain.Llm;

namespace BrainService.Services.Llm;

public interface ILlmProvider
{
    IAsyncEnumerable<LlmStreamChunk> StreamCompletionAsync(LlmRequest request, CancellationToken ct = default);
}
