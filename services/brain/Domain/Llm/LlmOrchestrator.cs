using System.Collections.Concurrent;
using System.Diagnostics;
using BrainService.Domain.Session;
using BrainService.Services.Llm;
using BrainService.Services.Session;

namespace BrainService.Domain.Llm;

public class LlmOrchestrator(
    LlmContextBuilder contextBuilder,
    GuildSettingsService settingsService,
    LlmProviderFactory providerFactory,
    VoiceSessionHistoryService historyService,
    ILogger<LlmOrchestrator> logger) : ILlmOrchestrationTrigger
{
    private VoiceSessionService? _sessionService;

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeCts = new();
    
    public void SetSessionService(VoiceSessionService sessionService) => _sessionService = sessionService;
    
    public Task TriggerAsync(string sessionId, ulong guildId, LlmContextReason reason)
    {
        _ = Task.Run(() => RunOrchestratorAsync(sessionId, guildId, reason));
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
    
    private async Task RunOrchestratorAsync(string sessionId, ulong guildId, LlmContextReason reason)
    {
        if (_sessionService == null)
        {
            logger.LogWarning("LlmOrchestrator has no SessionService - skipping");
            return;
        }

        // Cancel any existing call for this session
        Cancel(sessionId);

        var cts = new CancellationTokenSource();
        _activeCts[sessionId] = cts;
        var ct = cts.Token;

        var sw = Stopwatch.StartNew();
        string? eventId = null;
        var accumulated = string.Empty;

        try
        {
            // Get current session state for context building
            var state = await _sessionService.GetSessionStateAsync(sessionId);
            if (state == null)
            {
                logger.LogWarning("Session {SessionId} not found for LLM trigger", sessionId);
                return;
            }

            // Get pending events before the context build so they're included
            var pending = _sessionService.DrainPendingEvents(sessionId);

            // Build context
            var request = await contextBuilder.BuildAsync(state, reason, pending, ct);
            logger.LogInformation("[LLM] Session {SessionId} - context built ({MsgCount} messages)",
                sessionId, request.Messages.Count);

            // Resolve LLM provider for this guild
            var settings = await settingsService.GetSettingsAsync(guildId);
            var provider  = providerFactory.GetProvider(settings);

            // Transition: Thinking → Speaking on first token
            var firstToken = true;
            var sentenceCount = 0;

            await foreach (var chunk in provider.StreamCompletionAsync(request, ct))
            {
                if (chunk.IsComplete)
                {
                    // Final chunk
                    if (!string.IsNullOrEmpty(chunk.TextDelta))
                        accumulated += chunk.TextDelta;
                    break;
                }

                if (chunk.TextDelta == null) continue;

                accumulated += chunk.TextDelta;

                if (firstToken)
                {
                    firstToken = false;
                    // Transition state machine Thinking → Speaking
                    await _sessionService.FireConversationTriggerAsync(
                        sessionId, VoiceSessionMachineTrigger.LlmResponseStarted);

                    // Write initial partial BotResponse event
                    eventId = await historyService.AppendBotResponseAsync(
                        sessionId, accumulated, isPartial: true);
                }
                else if (eventId != null && accumulated.Length % 100 == 0)
                {
                    // Periodically upsert partial content so dashboard stays live
                    await historyService.AppendBotResponseAsync(
                        sessionId, accumulated, isPartial: true, existingEventId: eventId);
                }
            }

            ct.ThrowIfCancellationRequested();

            // Finalise BotResponse event
            sw.Stop();
            if (!string.IsNullOrEmpty(accumulated))
            {
                if (eventId == null)
                {
                    // LLM returned synchronously without streaming (rare)
                    eventId = await historyService.AppendBotResponseAsync(
                        sessionId, accumulated, isPartial: false,
                        finishReason: "stop", generationMs: (int)sw.ElapsedMilliseconds);
                }
                else
                {
                    await historyService.AppendBotResponseAsync(
                        sessionId, accumulated, isPartial: false,
                        finishReason: "stop", generationMs: (int)sw.ElapsedMilliseconds,
                        existingEventId: eventId);
                }

                logger.LogInformation("[LLM] Session {SessionId} - {Chars} chars in {Ms}ms",
                    sessionId, accumulated.Length, sw.ElapsedMilliseconds);
            }

            // Transition: Speaking → Idle
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
            if (_activeCts.TryRemove(sessionId, out var removed))
                removed.Dispose();
        }
    }
}