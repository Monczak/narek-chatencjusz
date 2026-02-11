using BrainService.Proto;
using BrainService.Services.Audio;
using BrainService.Services.Session;
using Grpc.Core;

namespace BrainService.Services;

public class BrainGrpcService(
    ILogger<BrainGrpcService> logger,
    NodeRegistryService nodeRegistry,
    CommandPublisher publisher,
    VoiceSessionService voiceSessionService,
    AudioGraphFactory audioGraphFactory,
    IHostApplicationLifetime applicationLifetime
) : Brain.BrainBase
{
    public override Task<PingResponse> Ping(PingRequest request, ServerCallContext context)
    {
        logger.LogInformation("Ping request received");
        return Task.FromResult(new PingResponse
        {
            Message = $"Pong: {request.Message}",
        });
    }

    public override async Task<JoinChannelResponse> JoinChannel(JoinChannelRequest request, 
        ServerCallContext context)
    {
        logger.LogInformation("Node {NodeId} requesting to join Guild {GuildName} ({GuildId}), Channel {ChannelName} ({ChannelId})", 
            request.NodeId, request.Guild.Name, request.Guild.Id, request.Channel.Name, request.Channel.Id);

        var currentOwner = await nodeRegistry.GetNodeForGuildAsync(request.Guild.Id);
        var currentChannelId = await nodeRegistry.GetChannelForGuildAsync(request.Guild.Id);
        var currentSessionId = await nodeRegistry.GetSessionForGuildAsync(request.Guild.Id);
        
        var requestedOrLatestSession = !string.IsNullOrEmpty(request.SessionId)
            ? request.SessionId
            : await nodeRegistry.GetLatestSessionForGuildAsync(request.Guild.Id);

        var hasOwner = !string.IsNullOrEmpty(currentOwner);
        var sameGuild = currentOwner == request.NodeId;
        var sameChannel = currentChannelId == request.Channel.Id;
        
        switch (hasOwner, sameGuild, sameChannel)
        {
            case (true, true, true):
                return new JoinChannelResponse 
                { 
                    Success = false, 
                    Message = "Already connected",
                    SessionId = currentSessionId ?? string.Empty
                };
            
            case (true, false, _):
                return new JoinChannelResponse 
                { 
                    Success = false, 
                    Message = $"Guild is already handled by node {currentOwner}",
                    SessionId = string.Empty
                };
            
            case (true, true, false) or (false, _, _):
                var sessionId = currentSessionId ?? requestedOrLatestSession ?? Guid.NewGuid().ToString();
                logger.LogInformation("Approved join for Node {NodeId} in Guild {GuildId} with Session {SessionId}", 
                    request.NodeId, request.Guild.Id, sessionId);

                var cmd = new BrainCommand
                {
                    Connect = new ConnectVoice
                    {
                        Guild = request.Guild,
                        Channel = request.Channel,
                        SessionId = sessionId,
                        CorrelationId = request.CorrelationId
                    }
                };
                await publisher.PublishCommandAsync(request.NodeId, cmd);
                
                return new JoinChannelResponse
                {
                    Success = true,
                    Message = "Connection approved",
                    SessionId = sessionId
                };
        }
    }

    public override async Task<LeaveChannelResponse> LeaveChannel(LeaveChannelRequest request,
        ServerCallContext context)
    {
        var currentOwner = await nodeRegistry.GetNodeForGuildAsync(request.Guild.Id);
        var currentSessionId = await nodeRegistry.GetSessionForGuildAsync(request.Guild.Id);
        var isOwner = currentOwner == request.NodeId;

        if (isOwner)
        {
            logger.LogInformation("Node {NodeId} leaving Guild {GuildId} Session {SessionId}", 
                request.NodeId, request.Guild.Id, currentSessionId);
        }

        if (isOwner || !string.IsNullOrEmpty(request.CorrelationId))
        {
            var cmd = new BrainCommand
            {
                Disconnect = new DisconnectVoice
                {
                    Guild = request.Guild,
                    SessionId = currentSessionId,
                    CorrelationId = request.CorrelationId,
                }
            };
            await publisher.PublishCommandAsync(request.NodeId, cmd);
        }
        
        return new LeaveChannelResponse { Success = true };
    }

    public override async Task<VoiceStateAck> NotifyVoiceState(VoiceStateNotification request, ServerCallContext context)
    {
        // Definitely connected
        var isConnectedState = request.Reason is VoiceStateReason.Connect 
            or VoiceStateReason.ManualMove 
            or VoiceStateReason.ReconcileMissing 
            or VoiceStateReason.ReconcileDrift;

        // Definitely disconnected
        var isDisconnectedState = request.Reason is VoiceStateReason.Disconnect 
            or VoiceStateReason.ManualDisconnect;

        if (isConnectedState && request.Channel != null)
        {
            var sessionId = request.SessionId;
            if (string.IsNullOrEmpty(sessionId))
            {
                sessionId = await nodeRegistry.GetSessionForGuildAsync(request.Guild.Id) ?? Guid.NewGuid().ToString();
                logger.LogInformation("Generated new session ID {SessionId} for untracked connection in Guild {GuildId}", 
                    sessionId, request.Guild.Id);
            }

            logger.LogInformation("State sync ({Reason}): Node {NodeId} confirmed connection in Guild {Guild} Channel {Channel} Session {SessionId}", 
                request.Reason, request.NodeId, request.Guild, request.Channel, sessionId);
            
            await nodeRegistry.RegisterGuildConnectionAsync(request.Guild, request.NodeId, request.Channel, sessionId);
            await voiceSessionService.UpdateSessionChannelAsync(request.Guild, request.Channel, sessionId);
        }
        else if (isDisconnectedState)
        {
            var currentOwner = await nodeRegistry.GetNodeForGuildAsync(request.Guild.Id);
            var isOwner = currentOwner == request.NodeId;
            
            if (isOwner)
            {
                var sessionId = request.SessionId ?? await nodeRegistry.GetSessionForGuildAsync(request.Guild.Id);
                logger.LogInformation("State sync ({Reason}): Node {NodeId} confirmed disconnection from Guild {Guild} Session {SessionId}", 
                    request.Reason, request.NodeId, request.Guild, sessionId);
                await nodeRegistry.UnregisterGuildConnectionAsync(request.Guild);
            }
        }
        else
        {
            logger.LogWarning("Received ambiguous voice state notification from {NodeId} for Guild {Guild}. Reason: {Reason}, HasChannel: {HasChannel}",
                request.NodeId, request.Guild, request.Reason, request.Channel != null);
        }

        return new VoiceStateAck { Success = true };
    }

    public override async Task<VoiceSessionEventAck> StreamVoiceSessionEvents(IAsyncStreamReader<VoiceSessionEvent> requestStream, ServerCallContext context)
    {
        var nodeId = context.RequestHeaders.GetValue("node_id");
        var activeGuilds = new HashSet<GuildContext>();
        
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(
            context.CancellationToken, 
            applicationLifetime.ApplicationStopping
        );

        try
        {
            await foreach (var voiceSessionEvent in requestStream.ReadAllAsync(cts.Token))
            {
                activeGuilds.Add(voiceSessionEvent.Guild);
                if (string.IsNullOrEmpty(nodeId))
                {
                    nodeId = voiceSessionEvent.NodeId;
                }

                await voiceSessionService.HandleEventAsync(voiceSessionEvent);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            logger.LogWarning("Voice session event stream for Node {NodeId} disconnected - marking {Count} active sessions as unstable",
                nodeId, activeGuilds.Count);

            if (!string.IsNullOrEmpty(nodeId) && activeGuilds.Count > 0)
            {
                foreach (var guild in activeGuilds)
                {
                    logger.LogInformation("Marking session in Guild {GuildId} as unstable due to Node {NodeId} disconnect", guild.Id, nodeId);
                    await voiceSessionService.HandleNodeDisconnectAsync(guild);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error reading voice event stream");
        }
        
        return new VoiceSessionEventAck { Success = true };
    }
    
    public override async Task StreamAudio(
        IAsyncStreamReader<UserAudioFrame> requestStream,
        IServerStreamWriter<AudioFrame> responseStream,
        ServerCallContext context)
    {
        var sessionId = context.RequestHeaders.GetValue("session_id");
        
        if (string.IsNullOrEmpty(sessionId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "session_id required in metadata"));
        }
        
        logger.LogInformation("Starting audio stream for session {SessionId}", sessionId);
        
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(
            context.CancellationToken,
            applicationLifetime.ApplicationStopping
        );
        
        try
        {
            // Create audio processing graph for this session
            var graph = await audioGraphFactory.CreateSessionGraphAsync(
                sessionId, requestStream, responseStream, cts.Token);
            
            await using (graph)
            {
                // Run the graph until canceled
                await graph.RunAsync();
            }
            
            logger.LogInformation("Audio stream ended for session {SessionId}", sessionId);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Audio stream cancelled for session {SessionId}", sessionId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in audio stream for session {SessionId}", sessionId);
            throw;
        }
    }
}
