using System.Collections.Concurrent;
using BrainService.Hubs;
using BrainService.Services.Asr;
using BrainService.Services.Audio.Nodes;
using BrainService.Services.Audio.Transport;
using BrainService.Services.Audio.Vad;
using BrainService.Services.Configuration;
using BrainService.Services.Session;
using Microsoft.AspNetCore.SignalR;

namespace BrainService.Services.Audio.Graph;

public sealed class AudioGraphFactory(
    UdpAudioServer udpServer,
    VoiceSessionService sessionService,
    BrainConfigService configService,
    SileroVadModelService vadModelService,
    AsrGrpcClient asrClient,
    VoiceSessionHistoryService historyService,
    SoundboardService soundboardService,
    ILoggerFactory loggerFactory,
    IHubContext<DashboardHub> hubContext) : IAsyncDisposable
{
    private readonly ILogger<AudioGraphFactory> _logger = loggerFactory.CreateLogger<AudioGraphFactory>();

    private readonly ConcurrentDictionary<string, (SessionAudioGraph Graph, Task Run, CancellationTokenSource Cts)>
        _active = new();

    public void StartSession(string sessionId, ulong guildId)
    {
        if (_active.ContainsKey(sessionId))
        {
            _logger.LogWarning("Audio graph for session {SessionId} is already running", sessionId);
            return;
        }

        if (!Guid.TryParse(sessionId, out var guid))
        {
            _logger.LogError("Invalid session ID format: {SessionId}", sessionId);
            return;
        }

        var cts = new CancellationTokenSource();
        var graph = new SessionAudioGraph(
            guid,
            guildId,
            udpServer,
            configService,
            vadModelService,
            sessionService,
            asrClient,
            historyService,
            loggerFactory,
            hubContext
        );

        udpServer.RegisterSession(guid, graph);

        var run = graph.RunAsync(cts.Token);
        _active[sessionId] = (graph, run, cts);

        _logger.LogInformation("Started audio graph for session {SessionId} guild {GuildId}", sessionId, guildId);
        
        _ = run.ContinueWith(t =>
        {
            if (t.IsFaulted)
                _logger.LogError(t.Exception, "Audio graph faulted for session {SessionId}", sessionId);
        }, TaskScheduler.Default);
    }

    public async Task StopSessionAsync(string sessionId)
    {
        if (!_active.TryRemove(sessionId, out var entry)) return;

        await entry.Cts.CancelAsync();

        try { await entry.Run.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch { /* best-effort */ }

        await entry.Graph.DisposeAsync();

        if (Guid.TryParse(sessionId, out var guid))
        {
            udpServer.UnregisterSession(guid);
        }

        entry.Cts.Dispose();
        _logger.LogInformation("Stopped audio graph for session {SessionId}", sessionId);
    }
    
    public SoundboardNode? TryGetSoundboard(string sessionId) =>
        _active.TryGetValue(sessionId, out var entry) ? entry.Graph.Soundboard : null;
    
    public TtsNode? TryGetTts(string sessionId) =>
        _active.TryGetValue(sessionId, out var entry) ? entry.Graph.Tts : null;

    public SessionAudioGraph? TryGetGraph(string sessionId) =>
        _active.TryGetValue(sessionId, out var entry) ? entry.Graph : null;

    public async ValueTask DisposeAsync()
    {
        _logger.LogInformation("Disposing all audio graphs ({Count} active)", _active.Count);
        var stops = _active.Keys.ToList().Select(StopSessionAsync);
        await Task.WhenAll(stops);
    }
}
