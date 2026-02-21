using System.Collections.Concurrent;
using BrainService.Domain.Session;
using BrainService.Services.Audio.Graph;
using BrainService.Services.Audio.Nodes;
using BrainService.Services.Llm;
using BrainService.Services.Guild;
using BrainService.Services.Session;

namespace BrainService.Services.Tts;

public class TtsResponseObserver(
    AudioGraphFactory audioGraphFactory,
    TtsVoiceRegistry voiceRegistry,
    TtsProviderFactory providerFactory,
    GuildSettingsService settingsService,
    ILogger<TtsResponseObserver> logger) : ILlmResponseObserver
{
    private sealed class ResponseState(TtsSynthesisWorker worker, CancellationTokenSource cts)
    {
        public TtsSynthesisWorker Worker { get; } = worker;
        public CancellationTokenSource Cts { get; } = cts;
        public SentenceBoundaryDetector Detector { get; } = new();
        public Task WorkerTask { get; set; } = Task.CompletedTask;
    }

    private readonly ConcurrentDictionary<string, ResponseState> _active = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _drainCts = new();

    private VoiceSessionService? _sessionService;

    public void SetSessionService(VoiceSessionService svc) => _sessionService = svc;

    public async Task OnResponseStarted(string sessionId, ulong guildId, CancellationToken ct)
    {
        var settings = await settingsService.GetSettingsAsync(guildId);
        var voice    = voiceRegistry.GetVoice(settings.TtsVoiceId);

        if (voice == null)
        {
            if (!string.IsNullOrEmpty(settings.TtsVoiceId))
                logger.LogWarning("[TTS] Session {SessionId} - voice '{VoiceId}' not in registry", sessionId, settings.TtsVoiceId);
            return;
        }

        var ttsNode    = audioGraphFactory.TryGetTts(sessionId);
        var soundboard = audioGraphFactory.TryGetSoundboard(sessionId);

        if (ttsNode == null)
        {
            logger.LogWarning("[TTS] Session {SessionId} - no TtsNode available", sessionId);
            return;
        }

        var provider = providerFactory.GetProvider(voice);
        var cts      = new CancellationTokenSource();
        var worker   = new TtsSynthesisWorker(
            ttsNode,
            soundboard!,
            provider,
            voice,
            settings.SoundboardDrainBufferMs,
            logger
        );

        var state = new ResponseState(worker, cts)
        {
            WorkerTask = worker.RunAsync(cts.Token)
        };

        // If a stale state somehow remains (e.g. prior response was force-canceled),
        // clean it up before storing the new one
        if (_active.TryRemove(sessionId, out var stale))
            CleanupState(stale);

        _active[sessionId] = state;
    }

    public void OnTextDelta(string sessionId, string delta)
    {
        if (!_active.TryGetValue(sessionId, out var state)) return;

        foreach (var sentence in state.Detector.Feed(delta))
            state.Worker.EnqueueSentence(sentence);
    }
    
    public async Task<bool> OnResponseCompletedAsync(string sessionId, CancellationToken ct)
    {
        if (!_active.TryRemove(sessionId, out var state)) return true;

        try
        {
            var trailing = state.Detector.Flush();
            if (!string.IsNullOrEmpty(trailing))
                state.Worker.EnqueueSentence(trailing);

            state.Worker.Complete();

            await state.WorkerTask.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            state.Cts.Dispose();
            throw;
        }
        finally
        {
            state.Cts.Dispose();
        }

        var ttsNode = audioGraphFactory.TryGetTts(sessionId);
        if (ttsNode == null || _sessionService == null || ttsNode.QueueDepth == 0)
        {
            // No audio was enqueued or no service wired up - complete immediately
            return true;
        }

        var drainCts = new CancellationTokenSource();
        if (_drainCts.TryRemove(sessionId, out var stale))
            stale.Cancel();
        _drainCts[sessionId] = drainCts;

        _ = Task.Run(() => DrainMonitorAsync(sessionId, ttsNode, drainCts.Token), CancellationToken.None);

        return false;
    }

    public string? OnResponseCanceled(string sessionId)
    {
        if (!_active.TryRemove(sessionId, out var state)) return null;

        var committedText = state.Worker.EnqueuedText;
        CleanupState(state);
        return string.IsNullOrEmpty(committedText) ? null : committedText;
    }
    
    public void CancelDrain(string sessionId)
    {
        if (_drainCts.TryRemove(sessionId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    private async Task DrainMonitorAsync(string sessionId, TtsNode ttsNode, CancellationToken drainCt)
    {
        const int pollMs = 20;

        try
        {
            while (ttsNode.QueueDepth > 0)
                await Task.Delay(pollMs, drainCt);
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("[TTS] Session {SessionId} - drain monitor cancelled", sessionId);
            return;
        }
        finally
        {
            _drainCts.TryRemove(sessionId, out _);
        }

        logger.LogDebug("[TTS] Session {SessionId} - playback finished, completing Speaking state", sessionId);

        try
        {
            await _sessionService!.FireConversationTriggerAsync(sessionId, VoiceSessionMachineTrigger.TtsPlaybackCompleted);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[TTS] Session {SessionId} - error firing TtsPlaybackCompleted", sessionId);
        }
    }

    private static void CleanupState(ResponseState state)
    {
        state.Worker.Complete();
        state.Cts.Cancel();
        state.Cts.Dispose();
    }
}
