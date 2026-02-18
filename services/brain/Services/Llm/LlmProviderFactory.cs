using System.Collections.Concurrent;
using BrainService.Domain.Llm;

namespace BrainService.Services.Llm;

public class LlmProviderFactory(OllamaModelService modelService, ILoggerFactory loggerFactory)
{
    private readonly ConcurrentDictionary<string, ILlmProvider> _cache = new();

    public ILlmProvider GetProvider(ResolvedLlmSettings settings)
    {
        // Cache per provider URL. Model name is resolved per-request inside OllamaLlmProvider.
        var key = settings.ProviderUrl;
        return _cache.GetOrAdd(key, url => new OllamaLlmProvider(
            url,
            settings.ModelName ?? string.Empty,
            loggerFactory.CreateLogger<OllamaLlmProvider>()));
    }
}
