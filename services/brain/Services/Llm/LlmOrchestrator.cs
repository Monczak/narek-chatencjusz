using System.Collections.Concurrent;
using System.Diagnostics;
using BrainService.Domain.Llm;
using BrainService.Domain.Session;
using BrainService.Services.Session;

namespace BrainService.Services.Llm;

public class LlmOrchestrator(
    LlmContextBuilder contextBuilder,
    GuildSettingsService settingsService,
    LlmProviderFactory providerFactory,
    VoiceSessionHistoryService historyService,
    ToolContextAccessor toolContextAccessor,
    ILogger<LlmOrchestrator> logger) : ILlmOrchestrationTrigger
{
    private VoiceSessionService? _sessionService;

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeCts = new();
    
    public void SetSessionService(VoiceSessionService sessionService) => _sessionService = sessionService;
    
    public Task TriggerAsync(string sessionId, ulong guildId, LlmContextReason reason)
    {
        _ = Task.Run(() => RunAsync(sessionId, guildId, reason));
        return Task.CompletedTask;
    }

    public void Cancel(string sessionId)
    {
        if (_activeCts.TryRemove(sessionId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }
    
    private async Task RunAsync(string sessionId, ulong guildId, LlmContextReason reason)
    {
        if (_sessionService == null)
        {
            logger.LogWarning("LlmOrchestrator has no SessionService - skipping");
            return;
        }

        Cancel(sessionId);

        var cts = new CancellationTokenSource();
        _activeCts[sessionId] = cts;
        var ct = cts.Token;

        var sw = Stopwatch.StartNew();
        string? eventId = null;
        var accumulated = string.Empty;
        var observedToolCalls = new List<LlmToolCall>();

        try
        {
            var state = await _sessionService.GetSessionStateAsync(sessionId);
            if (state == null)
            {
                logger.LogWarning("Session {SessionId} not found for LLM trigger", sessionId);
                return;
            }

            var pending = _sessionService.DrainPendingEvents(sessionId);

            var request = await contextBuilder.BuildAsync(state, reason, pending, ct);
            logger.LogInformation("[LLM] Session {SessionId} - context built ({MsgCount} messages)",
                sessionId, request.Messages.Count);

            var settings = await settingsService.GetSettingsAsync(guildId);
            var provider = providerFactory.GetProvider(settings);
            
            toolContextAccessor.Current = new ToolExecutionContext
            {
                SessionId = sessionId,
                GuildId = guildId,
                SessionState = state,
            };

            var firstToken = true;
            var sentenceCount = 0;
            
            await foreach (var chunk in provider.StreamCompletionAsync(request, ct))
            {
                if (chunk.IsComplete)
                {
                    if (!string.IsNullOrEmpty(chunk.TextDelta))
                        accumulated += chunk.TextDelta;
                    break;
                }

                // Tool call observed (already executed by middleware) - collect for history.
                if (chunk.ToolCall != null)
                {
                    observedToolCalls.Add(chunk.ToolCall);
                    logger.LogInformation("[LLM] Session {SessionId} - tool '{Name}' called",
                        sessionId, chunk.ToolCall.Name);
                    continue;
                }

                if (chunk.TextDelta == null) continue;

                accumulated += chunk.TextDelta;

                if (firstToken)
                {
                    firstToken = false;
                    await _sessionService.FireConversationTriggerAsync(
                        sessionId, VoiceSessionMachineTrigger.LlmResponseStarted);

                    eventId = await historyService.AppendBotResponseAsync(
                        sessionId, accumulated, isPartial: true,
                        finishReason: LlmFinishReason.Cancelled, generationMs: 0);
                }
                else
                {
                    sentenceCount++;
                    if (sentenceCount % 20 == 0 && eventId != null)
                    {
                        await historyService.AppendBotResponseAsync(
                            sessionId, accumulated, isPartial: true,
                            finishReason: LlmFinishReason.Cancelled, generationMs: 0,
                            existingEventId: eventId);
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(accumulated))
            {
                IReadOnlyList<object>? storedToolCalls = observedToolCalls.Count > 0
                    ? observedToolCalls.Select(tc => (object)new
                    {
                        id = tc.Id,
                        name = tc.Name,
                        arguments = tc.ArgumentsJson,
                    }).ToList()
                    : null;

                await historyService.AppendBotResponseAsync(
                    sessionId, accumulated, isPartial: false,
                    finishReason: LlmFinishReason.Stop,
                    generationMs: (int)sw.ElapsedMilliseconds,
                    toolCalls: storedToolCalls,
                    existingEventId: eventId);
            }

            logger.LogInformation("[LLM] Session {SessionId} - {Chars} chars in {Ms}ms ({Tools} tool call(s))",
                sessionId, accumulated.Length, sw.ElapsedMilliseconds, observedToolCalls.Count);

            await _sessionService.FireConversationTriggerAsync(
                sessionId, VoiceSessionMachineTrigger.LlmResponseCompleted);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("[LLM] Session {SessionId} - cancelled after {Ms}ms",
                sessionId, sw.ElapsedMilliseconds);

            if (eventId != null)
                await historyService.MarkBotResponseCanceledAsync(sessionId, eventId, accumulated);

            await _sessionService!.FireConversationTriggerAsync(
                sessionId, VoiceSessionMachineTrigger.LlmCanceled);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[LLM] Session {SessionId} - unhandled error", sessionId);

            await _sessionService!.FireConversationTriggerAsync(
                sessionId, VoiceSessionMachineTrigger.LlmCanceled);
        }
        finally
        {
            toolContextAccessor.Current = null;

            if (_activeCts.TryRemove(sessionId, out var removed))
                removed.Dispose();
        }
    }
}
