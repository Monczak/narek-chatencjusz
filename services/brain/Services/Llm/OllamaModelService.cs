using System.Text.Json;
using OllamaSharp;

namespace BrainService.Services.Llm;

public class OllamaModelService
{
    private readonly OllamaApiClient _ollama;
    private readonly HttpClient _http;
    private readonly ILogger<OllamaModelService> _logger;

    private List<string> _cachedModelNames = [];
    private DateTime _cacheExpiry = DateTime.MinValue;
    private readonly TimeSpan _cacheTtl = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _lock = new(1, 1);

    public OllamaModelService(string ollamaUrl, IHttpClientFactory httpClientFactory, ILogger<OllamaModelService> logger)
    {
        _logger = logger;
        _ollama = new OllamaApiClient(new Uri(ollamaUrl.TrimEnd('/')));
        _http = httpClientFactory.CreateClient("OllamaModel");
        _http.BaseAddress = new Uri(ollamaUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(10);
    }

    public OllamaApiClient ApiClient => _ollama;

    public async Task<IReadOnlyList<string>> GetAvailableModelsAsync(CancellationToken ct = default)
    {
        if (DateTime.UtcNow < _cacheExpiry)
            return _cachedModelNames;

        await _lock.WaitAsync(ct);
        try
        {
            if (DateTime.UtcNow < _cacheExpiry)
                return _cachedModelNames;

            var models = await _ollama.ListLocalModelsAsync(ct);
            _cachedModelNames = models.Select(m => m.Name).ToList();
            _cacheExpiry = DateTime.UtcNow + _cacheTtl;
            return _cachedModelNames;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to list Ollama models");
            return _cachedModelNames;
        }
        finally
        {
            _lock.Release();
        }
    }
    
    public async Task<int?> GetContextWindowAsync(string? modelName, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(modelName))
            return null;

        try
        {
            var body = JsonSerializer.Serialize(new { name = modelName });
            var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            var response = await _http.PostAsync("api/show", content, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            
            if (!doc.RootElement.TryGetProperty("parameters", out var paramEl)
                || paramEl.ValueKind != JsonValueKind.String)
                return null;

            foreach (var line in paramEl.GetString()!.Split('\n'))
            {
                var parts = line.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && parts[0] == "num_ctx" && int.TryParse(parts[1].Trim(), out var ctx))
                    return ctx;
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to query context window for model {Model}", modelName);
            return null;
        }
    }
}
