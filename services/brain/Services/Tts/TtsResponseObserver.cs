using System.Collections.Concurrent;
using BrainService.Services.Audio.Graph;
using BrainService.Services.Llm;
using BrainService.Services.Guild;

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

    public async Task OnResponseCompletedAsync(string sessionId, CancellationToken ct)
    {
        if (!_active.TryRemove(sessionId, out var state)) return;

        try
        {
            var trailing = state.Detector.Flush();
            if (!string.IsNullOrEmpty(trailing))
                state.Worker.EnqueueSentence(trailing);

            state.Worker.Complete();

            await state.WorkerTask.WaitAsync(ct);
        }
        catch (OperationCanceledException) { /* caller cancelled */ }
        finally
        {
            state.Cts.Dispose();
        }
    }

    public string? OnResponseCanceled(string sessionId)
    {
        if (!_active.TryRemove(sessionId, out var state)) return null;

        var committedText = state.Worker.EnqueuedText;
        CleanupState(state);
        return string.IsNullOrEmpty(committedText) ? null : committedText;
    }

    private static void CleanupState(ResponseState state)
    {
        state.Worker.Complete();
        state.Cts.Cancel();
        state.Cts.Dispose();
    }
}
