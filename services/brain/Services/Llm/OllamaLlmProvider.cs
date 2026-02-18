using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using BrainService.Domain.Llm;
using Microsoft.Extensions.AI;
using OllamaSharp;

namespace BrainService.Services.Llm;

public class OllamaLlmProvider(string ollamaUrl, string defaultModel, ILogger<OllamaLlmProvider> logger) : ILlmProvider
{
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
        
        var client = new ChatClientBuilder(new OllamaApiClient(ollamaUrl, modelName))
            .UseFunctionInvocation()
            .Build();

        var messages = request.Messages.Select(ToMeaiMessage).ToList();

        var options = new ChatOptions
        {
            Temperature = request.Settings.Temperature,
            MaxOutputTokens = request.Settings.MaxTokens,
            TopP = request.Settings.TopP,
        };

        if (request.Tools is { Count: > 0 })
            options.Tools = [.. request.Tools];

        try
        {
            await foreach (var update in client.GetStreamingResponseAsync(messages, options, ct))
            {
                // FunctionCallContent: the middleware has already invoked the tool,
                // but we yield the chunk so the orchestrator can log it for history.
                var funcCall = update.Contents?.OfType<FunctionCallContent>().FirstOrDefault();
                if (funcCall != null)
                {
                    var argsJson = funcCall.Arguments != null
                        ? JsonSerializer.Serialize(funcCall.Arguments)
                        : "{}";
                    await writer.WriteAsync(new LlmStreamChunk(
                        update.Text,
                        new LlmToolCall(funcCall.CallId ?? "", funcCall.Name, argsJson),
                        false,
                        null), ct);
                    continue;
                }

                // FunctionResultContent: skip - orchestrator doesn't need these.
                if (update.Contents?.OfType<FunctionResultContent>().Any() == true)
                    continue;

                if (update.FinishReason != null)
                {
                    var reason = update.FinishReason switch
                    {
                        var r when r == ChatFinishReason.Stop => LlmFinishReason.Stop,
                        var r when r == ChatFinishReason.Length => LlmFinishReason.Length,
                        var r when r == ChatFinishReason.ToolCalls => LlmFinishReason.ToolCalls,
                        _ => LlmFinishReason.Stop
                    };
                    await writer.WriteAsync(new LlmStreamChunk(update.Text, null, true, reason), ct);
                    writer.Complete();
                    return;
                }

                if (!string.IsNullOrEmpty(update.Text))
                    await writer.WriteAsync(new LlmStreamChunk(update.Text, null, false, null), ct);
            }

            writer.Complete();
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
