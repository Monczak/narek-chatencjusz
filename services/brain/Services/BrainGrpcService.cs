using BrainService.Proto.Brain;
using BrainService.Services.Audio.Graph;
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
        => Task.FromResult(new PingResponse { Message = $"Pong: {request.Message}" });

    public override async Task<JoinChannelResponse> JoinChannel(JoinChannelRequest request, ServerCallContext context)
    {
        logger.LogInformation("Node {NodeId} requesting join Guild {GuildId} Channel {ChannelId}",
            request.NodeId, request.Guild.Id, request.Channel.Id);

        var currentOwner     = await nodeRegistry.GetNodeForGuildAsync(request.Guild.Id);
        var currentChannelId = await nodeRegistry.GetChannelForGuildAsync(request.Guild.Id);
        var currentSessionId = await nodeRegistry.GetSessionForGuildAsync(request.Guild.Id);

        var requestedSession = !string.IsNullOrEmpty(request.SessionId)
            ? request.SessionId
            : await nodeRegistry.GetLatestSessionForGuildAsync(request.Guild.Id);

        var hasOwner    = !string.IsNullOrEmpty(currentOwner);
        var sameGuild   = currentOwner == request.NodeId;
        var sameChannel = currentChannelId == request.Channel.Id;

        switch (hasOwner, sameGuild, sameChannel)
        {
            case (true, true, true):
                return new JoinChannelResponse
                {
                    Success   = false,
                    Message   = "Already connected",
                    SessionId = currentSessionId ?? ""
                };

            case (true, false, _):
                return new JoinChannelResponse
                {
                    Success = false,
                    Message = $"Guild handled by node {currentOwner}"
                };

            default:
                var sessionId = currentSessionId ?? requestedSession ?? Guid.NewGuid().ToString();
                await publisher.PublishCommandAsync(request.NodeId, new BrainCommand
                {
                    Connect = new ConnectVoice
                    {
                        Guild          = request.Guild,
                        Channel        = request.Channel,
                        SessionId      = sessionId,
                        CorrelationId  = request.CorrelationId
                    }
                });
                return new JoinChannelResponse
                {
                    Success   = true,
                    Message   = "Connection approved",
                    SessionId = sessionId
                };
        }
    }

    public override async Task<LeaveChannelResponse> LeaveChannel(LeaveChannelRequest request, ServerCallContext context)
    {
        var currentOwner     = await nodeRegistry.GetNodeForGuildAsync(request.Guild.Id);
        var currentSessionId = await nodeRegistry.GetSessionForGuildAsync(request.Guild.Id);

        if (currentOwner == request.NodeId || !string.IsNullOrEmpty(request.CorrelationId))
        {
            await publisher.PublishCommandAsync(request.NodeId, new BrainCommand
            {
                Disconnect = new DisconnectVoice
                {
                    Guild          = request.Guild,
                    SessionId      = currentSessionId ?? "",
                    CorrelationId  = request.CorrelationId,
                }
            });
        }
        return new LeaveChannelResponse { Success = true };
    }

    public override async Task<VoiceStateAck> NotifyVoiceState(VoiceStateNotification request, ServerCallContext context)
    {
        var isConnected = request.Reason is
            VoiceStateReason.Connect or VoiceStateReason.ManualMove or
            VoiceStateReason.ReconcileMissing or VoiceStateReason.ReconcileDrift;

        var isDisconnected = request.Reason is
            VoiceStateReason.Disconnect or VoiceStateReason.ManualDisconnect;

        if (isConnected && request.Channel != null)
        {
            var sessionId = request.SessionId;
            if (string.IsNullOrEmpty(sessionId))
                sessionId = await nodeRegistry.GetSessionForGuildAsync(request.Guild.Id)
                            ?? Guid.NewGuid().ToString();

            await nodeRegistry.RegisterGuildConnectionAsync(
                request.Guild, request.NodeId, request.Channel, sessionId);
            await voiceSessionService.UpdateSessionChannelAsync(
                request.Guild, request.Channel, sessionId);
            
            audioGraphFactory.StartSession(sessionId, request.Guild.Id);

            logger.LogInformation("Graph started for session {SessionId} guild {GuildId}",
                sessionId, request.Guild.Id);
        }
        else if (isDisconnected)
        {
            var currentOwner = await nodeRegistry.GetNodeForGuildAsync(request.Guild.Id);
            if (currentOwner == request.NodeId)
            {
                var sessionId = request.SessionId
                                ?? await nodeRegistry.GetSessionForGuildAsync(request.Guild.Id);
                if (!string.IsNullOrEmpty(sessionId))
                    await audioGraphFactory.StopSessionAsync(sessionId);

                await nodeRegistry.UnregisterGuildConnectionAsync(request.Guild);
            }
        }

        return new VoiceStateAck { Success = true };
    }

    public override async Task<VoiceSessionEventAck> StreamVoiceSessionEvents(
        IAsyncStreamReader<VoiceSessionEvent> requestStream, ServerCallContext context)
    {
        var nodeId       = context.RequestHeaders.GetValue("node_id");
        var activeGuilds = new HashSet<GuildContext>();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(
            context.CancellationToken, applicationLifetime.ApplicationStopping);

        try
        {
            await foreach (var evt in requestStream.ReadAllAsync(cts.Token))
            {
                activeGuilds.Add(evt.Guild);
                if (string.IsNullOrEmpty(nodeId)) nodeId = evt.NodeId;
                await voiceSessionService.HandleEventAsync(evt);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            logger.LogWarning("Event stream for Node {NodeId} disconnected", nodeId);

            if (!string.IsNullOrEmpty(nodeId))
            {
                foreach (var guild in activeGuilds)
                {
                    await voiceSessionService.HandleNodeDisconnectAsync(guild);
                    
                    var sessionId = await nodeRegistry.GetSessionForGuildAsync(guild.Id);
                    if (!string.IsNullOrEmpty(sessionId))
                    {
                        logger.LogInformation(
                            "Stopping audio graph for session {SessionId} after node {NodeId} disconnect",
                            sessionId, nodeId);
                        await audioGraphFactory.StopSessionAsync(sessionId);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error reading event stream");
        }

        return new VoiceSessionEventAck { Success = true };
    }
}
