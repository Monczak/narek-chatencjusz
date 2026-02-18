using System.Collections.Concurrent;
using BrainService.Domain.Llm;

namespace BrainService.Services.Llm;

public class LlmProviderFactory(ILoggerFactory loggerFactory)
{
    private readonly ConcurrentDictionary<string, ILlmProvider> _cache = new();

    public ILlmProvider GetProvider(ResolvedLlmSettings settings)
    {
        var key = settings.ProviderUrl;
        return _cache.GetOrAdd(key, url => new OllamaLlmProvider(
            url,
            settings.ModelName ?? string.Empty,
            loggerFactory.CreateLogger<OllamaLlmProvider>()));
    }
}
