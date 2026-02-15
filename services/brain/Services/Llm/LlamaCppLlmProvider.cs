using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using BrainService.Domain.Llm;

namespace BrainService.Services.Llm;

public class LlamaCppLlmProvider : ILlmProvider
{
    private readonly HttpClient _http;
    private readonly ILogger<LlamaCppLlmProvider> _logger;

    public bool SupportsTokenCounting => true;

    public LlamaCppLlmProvider(string baseUrl, IHttpClientFactory httpClientFactory,
        ILogger<LlamaCppLlmProvider> logger)
    {
        _logger = logger;
        _http = httpClientFactory.CreateClient("LlamaCppLlm");
        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromMinutes(5);
    }

    public async IAsyncEnumerable<LlmStreamChunk> StreamCompletionAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var body = new
        {
            model = "default",
            stream = true,
            temperature = request.Settings.Temperature,
            max_tokens = request.Settings.MaxTokens,
            top_p = request.Settings.TopP,
            repeat_penalty = request.Settings.RepetitionPenalty,
            messages = request.Messages.Select(m => new { role = m.Role, content = m.Content }).ToList()
        };

        var json = JsonSerializer.Serialize(body);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        
        HttpResponseMessage? response = null;
        Exception? requestException = null;
        try
        {
            response = await _http.PostAsync("v1/chat/completions", content, ct);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            requestException = ex;
        }

        if (requestException != null)
        {
            _logger.LogError(requestException, "LLM request failed");
            yield return new LlmStreamChunk(null, null, true, "stop");
            yield break;
        }
        
        await using var stream = await response!.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!ct.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (line == null) break;
            if (!line.StartsWith("data: ")) continue;
            
            var data = line["data: ".Length..].Trim();
            if (data == "[DONE]")
            {
                yield return new LlmStreamChunk(null, null, true, "stop");
                yield break;
            }
            
            LlmStreamChunk? chunk = null;
            try
            {
                chunk = ParseSseDelta(data);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to parse SSE delta: {Data}", data);
            }

            if (chunk != null)
            {
                yield return chunk;
            }
        }
        
        yield return new LlmStreamChunk(null, null, true, "cancelled");
    }

    private LlmStreamChunk? ParseSseDelta(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var choices = root.GetProperty("choices");
        if (choices.GetArrayLength() == 0)
        {
            return new LlmStreamChunk(null, null, false, null);
        }

        var choice = choices[0];
        var finishReason = choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind != JsonValueKind.Null
            ? reason.GetString()
            : null;

        var delta = choice.GetProperty("delta");

        if (delta.TryGetProperty("tool_calls", out var calls) && calls.GetArrayLength() > 0)
        {
            var first = calls[0];
            var name = first.TryGetProperty("function", out var fn) ? fn.GetProperty("name").GetString() ?? "" : "";
            var argsRaw = first.TryGetProperty("function", out var fn2) ? fn2.GetProperty("name").GetString() ?? "" : "";
            var id = first.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
            
            return new LlmStreamChunk(null, new LlmToolCall(id, name, argsRaw), finishReason != null, finishReason);
        }

        var text = delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString()
            : null;
        
        return new LlmStreamChunk(text, null, finishReason != null, finishReason);
    }
}