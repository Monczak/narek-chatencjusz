using System.Collections.Concurrent;
using BrainService.Domain.Llm;

namespace BrainService.Services.Llm;

public class LlmProviderFactory(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
{
    private readonly ConcurrentDictionary<string, ILlmProvider> _providerCache = new();
    private readonly ConcurrentDictionary<string, ITokenCounter> _counterCache = new();

    public ILlmProvider GetProvider(ResolvedLlmSettings settings)
    {
        var key = $"{settings.ProviderType}|{settings.LlmUrl}";
        return _providerCache.GetOrAdd(key, _ => CreateProvider(settings));
    }
    
    public ITokenCounter GetTokenCounter(ResolvedLlmSettings settings)
    {
        var key = $"{settings.ProviderType}|{settings.LlmUrl}";
        return _counterCache.GetOrAdd(key, _ => CreateCounter(settings));
    }

    private ILlmProvider CreateProvider(ResolvedLlmSettings settings) =>
        settings.ProviderType switch
        {
            LlmProviderType.LlamaCpp => new LlamaCppLlmProvider(
                settings.LlmUrl,
                httpClientFactory,
                loggerFactory.CreateLogger<LlamaCppLlmProvider>()),
            _ => throw new NotSupportedException($"Unknown LLM provider type: {settings.ProviderType}")
        };

    private ITokenCounter CreateCounter(ResolvedLlmSettings settings) =>
        settings.ProviderType switch
        {
            LlmProviderType.LlamaCpp => new LlamaCppTokenCounter(
                settings.LlmUrl,
                httpClientFactory,
                loggerFactory.CreateLogger<LlamaCppTokenCounter>()),
            _ => new FallbackTokenCounter()
        };
}