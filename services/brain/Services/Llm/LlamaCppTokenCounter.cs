using System.Text;
using System.Text.Json;

namespace BrainService.Services.Llm;

public class LlamaCppTokenCounter : ITokenCounter
{
    private readonly HttpClient _http;
    private readonly ITokenCounter _fallback = new FallbackTokenCounter();
    private readonly ILogger<LlamaCppTokenCounter> _logger;
    public bool IsAvailable => true;

    public LlamaCppTokenCounter(string baseUrl, IHttpClientFactory httpClientFactory,
        ILogger<LlamaCppTokenCounter> logger)
    {
        _logger = logger;
        _http = httpClientFactory.CreateClient("LlamaCppLlmProvider");
        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(10);
    }
    
    public async Task<int> CountTokensAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        try
        {
            var body = JsonSerializer.Serialize(new { content = text });
            var content = new StringContent(body, Encoding.UTF8, "application/json");
            var response = await _http.PostAsync("tokenize", content, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            return doc.RootElement.GetProperty("tokens").GetArrayLength();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Tokenizer endpoint unavailable - falling back to estimate");
            return await _fallback.CountTokensAsync(text, ct);
        }
    }

}