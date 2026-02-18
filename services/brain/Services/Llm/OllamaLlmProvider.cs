using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using BrainService.Domain.Llm;
using Microsoft.Extensions.AI;
using OllamaSharp;

namespace BrainService.Services.Llm;

public class OllamaLlmProvider(string ollamaUrl, string defaultModel, ILogger<OllamaLlmProvider> logger) : ILlmProvider
{
    private const int MaxToolRounds = 5;

    public async IAsyncEnumerable<LlmStreamChunk> StreamCompletionAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = Channel.CreateUnbounded<LlmStreamChunk>(new UnboundedChannelOptions { SingleWriter = true });
        
        _ = ProduceAsync(channel.Writer, request, ct);

        await foreach (var chunk in channel.Reader.ReadAllAsync(ct))
            yield return chunk;
    }

    private async Task ProduceAsync(
        ChannelWriter<LlmStreamChunk> writer,
        LlmRequest request,
        CancellationToken ct)
    {
        var modelName = request.Settings.ModelName ?? defaultModel;

        // Raw client - no middleware. We handle the tool call loop ourselves so we
        // can call AIFunction.InvokeAsync directly and have full control over execution.
        IChatClient client = new OllamaApiClient(ollamaUrl, modelName);

        // Mutable message list - extended with assistant + tool messages each round.
        var messages = request.Messages.Select(ToMeaiMessage).ToList();

        var options = new ChatOptions
        {
            Temperature = request.Settings.Temperature,
            MaxOutputTokens = request.Settings.MaxTokens,
            TopP = request.Settings.TopP,
        };

        if (request.Tools is { Count: > 0 })
            options.Tools = [.. request.Tools];

        logger.LogDebug("[LLM] Sending {Count} tool(s) to model {Model}: {Names}",
            options.Tools?.Count ?? 0,
            modelName,
            options.Tools != null ? string.Join(", ", options.Tools.Select(t => t.Name)) : "none");

        try
        {
            for (var round = 0; round <= MaxToolRounds; round++)
            {
                var collectedFunctionCalls = new List<FunctionCallContent>();
                var roundText = string.Empty;

                await foreach (var update in client.GetStreamingResponseAsync(messages, options, ct))
                {
                    // Collect any function calls; also capture any text that preceded them.
                    var funcCall = update.Contents?.OfType<FunctionCallContent>().FirstOrDefault();
                    if (funcCall != null)
                    {
                        collectedFunctionCalls.Add(funcCall);
                        if (!string.IsNullOrEmpty(update.Text))
                            roundText += update.Text;
                        continue;
                    }

                    if (update.FinishReason != null)
                    {
                        if (!string.IsNullOrEmpty(update.Text))
                            roundText += update.Text;
                        break;
                    }

                    if (!string.IsNullOrEmpty(update.Text))
                    {
                        // Yield text to the orchestrator as it streams.
                        roundText += update.Text;
                        await writer.WriteAsync(new LlmStreamChunk(update.Text, null, false, null), ct);
                    }
                }

                if (collectedFunctionCalls.Count == 0)
                {
                    // No tool calls - normal end of stream.
                    // Any remaining roundText was already yielded above chunk-by-chunk;
                    // the IsComplete sentinel closes the stream for the orchestrator.
                    await writer.WriteAsync(new LlmStreamChunk(null, null, true, LlmFinishReason.Stop), ct);
                    writer.Complete();
                    return;
                }

                if (round == MaxToolRounds)
                {
                    logger.LogWarning("[LLM] Model {Model} hit tool round limit ({Max})", modelName, MaxToolRounds);
                    await writer.WriteAsync(new LlmStreamChunk(null, null, true, LlmFinishReason.Stop), ct);
                    writer.Complete();
                    return;
                }

                logger.LogInformation("[LLM] Model {Model} - executing {Count} tool call(s) (round {Round})",
                    modelName, collectedFunctionCalls.Count, round + 1);

                // Build the assistant message that contains the function calls
                // (and any text that came before them in the same response).
                var assistantContents = new List<AIContent>();
                if (!string.IsNullOrEmpty(roundText))
                    assistantContents.Add(new TextContent(roundText));
                foreach (var fc in collectedFunctionCalls)
                    assistantContents.Add(fc);
                messages.Add(new ChatMessage(ChatRole.Assistant, assistantContents));

                // Invoke each tool and append its result.
                foreach (var fc in collectedFunctionCalls)
                {
                    var result = await InvokeToolAsync(fc, options, ct);

                    logger.LogInformation("[LLM] Tool '{Name}' ({CallId}) -> {Result}", fc.Name, fc.CallId, result);

                    // Yield to the orchestrator so it can record it in history.
                    var argsJson = fc.Arguments != null ? JsonSerializer.Serialize(fc.Arguments) : "{}";
                    await writer.WriteAsync(new LlmStreamChunk(
                        null,
                        new LlmToolCall(fc.CallId ?? string.Empty, fc.Name, argsJson),
                        false,
                        null), ct);

                    messages.Add(new ChatMessage(ChatRole.Tool,
                        [new FunctionResultContent(fc.CallId ?? string.Empty, result)]));
                }

                // Loop: re-call the model with the extended message list.
            }
        }
        catch (OperationCanceledException)
        {
            writer.Complete();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ollama streaming error for model {Model}", modelName);
            await writer.WriteAsync(new LlmStreamChunk(null, null, true, LlmFinishReason.Error), CancellationToken.None);
            writer.Complete();
        }
    }

    private async Task<string> InvokeToolAsync(FunctionCallContent fc, ChatOptions options, CancellationToken ct)
    {
        var aiFunction = options.Tools?
            .OfType<AIFunction>()
            .FirstOrDefault(f => string.Equals(f.Name, fc.Name, StringComparison.OrdinalIgnoreCase));

        if (aiFunction == null)
        {
            logger.LogWarning("[LLM] Tool '{Name}' not found in registered functions", fc.Name);
            return $"Tool '{fc.Name}' is not available.";
        }

        try
        {
            var args = new AIFunctionArguments();
            if (fc.Arguments != null)
                foreach (var kvp in fc.Arguments)
                    args[kvp.Key] = kvp.Value;
            var result = await aiFunction.InvokeAsync(args, ct);
            return result?.ToString() ?? string.Empty;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[LLM] Tool '{Name}' threw an exception", fc.Name);
            return $"Error executing '{fc.Name}': {ex.Message}";
        }
    }

    private static ChatMessage ToMeaiMessage(LlmMessage msg)
    {
        var role = msg.Role switch
        {
            "system" => ChatRole.System,
            "assistant" => ChatRole.Assistant,
            "tool" => ChatRole.Tool,
            _ => ChatRole.User
        };
        return new ChatMessage(role, msg.Content);
    }
}
