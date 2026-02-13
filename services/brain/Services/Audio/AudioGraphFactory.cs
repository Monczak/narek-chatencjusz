using System.Collections.Concurrent;
using BrainService.Services.Audio.Transport;
using BrainService.Services.Configuration;
using BrainService.Services.Session;

namespace BrainService.Services.Audio;

/// <summary>
/// Owns all running <see cref="SessionAudioGraph"/> instances.
/// Sessions are started when the bot confirms it has joined a channel
/// and stopped on disconnect or application shutdown.
/// </summary>
public sealed class AudioGraphFactory(
    UdpAudioServer udpServer,
    VoiceSessionService sessionService,
    BrainConfigService configService,
    SileroVadModelService vadModelService,
    ILoggerFactory loggerFactory) : IAsyncDisposable
{
    private readonly ILogger<AudioGraphFactory> _logger = loggerFactory.CreateLogger<AudioGraphFactory>();

    private readonly ConcurrentDictionary<string, (SessionAudioGraph Graph, Task Run, CancellationTokenSource Cts)>
        _active = new();

    public async Task StartSessionAsync(string sessionId, ulong guildId)
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

        var cts   = new CancellationTokenSource();
        var graph = new SessionAudioGraph(guid, guildId, udpServer, configService, vadModelService, loggerFactory);

        udpServer.RegisterSession(guid, graph);

        var run = graph.RunAsync(cts.Token);
        _active[sessionId] = (graph, run, cts);

        _logger.LogInformation("Started audio graph for session {SessionId} guild {GuildId}", sessionId, guildId);

        // Detach — RunAsync completes when cancelled; don't await it here
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
            udpServer.UnregisterSession(guid);

        entry.Cts.Dispose();
        _logger.LogInformation("Stopped audio graph for session {SessionId}", sessionId);
    }

    public async ValueTask DisposeAsync()
    {
        _logger.LogInformation("Disposing all audio graphs ({Count} active)", _active.Count);
        var stops = _active.Keys.ToList().Select(id => StopSessionAsync(id));
        await Task.WhenAll(stops);
    }
}
