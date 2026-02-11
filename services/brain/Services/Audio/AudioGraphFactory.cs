using BrainService.Hubs;
using BrainService.Proto;
using BrainService.Services.Configuration;
using BrainService.Services.Session;
using Grpc.Core;
using Microsoft.AspNetCore.SignalR;

namespace BrainService.Services.Audio;

public class AudioGraphFactory(
    VoiceSessionService sessionService,
    BrainConfigService configService,
    SileroVadModelService vadModelService,
    IHubContext<DashboardHub> hub,
    ILoggerFactory loggerFactory)
{
    private readonly ILogger<AudioGraphFactory> _logger = loggerFactory.CreateLogger<AudioGraphFactory>();

    public async Task<SessionAudioGraph> CreateSessionGraphAsync(
        string sessionId,
        IAsyncStreamReader<UserAudioFrame> botInputStream,
        IServerStreamWriter<AudioFrame> botOutputStream,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Creating audio graph for session {SessionId}", sessionId);
        
        var sessionState = await sessionService.GetSessionStateAsync(sessionId);
        if (sessionState == null)
        {
            throw new InvalidOperationException($"Session {sessionId} not found");
        }
        
        var graph = new SessionAudioGraph(
            sessionId,
            sessionState.GuildId,
            botInputStream,
            botOutputStream,
            sessionService,
            configService,
            vadModelService,
            hub,
            loggerFactory
        );
        
        _logger.LogInformation("Audio graph created for session {SessionId}, guild {GuildId}",
            sessionId, sessionState.GuildId);
        
        return graph;
    }
}
