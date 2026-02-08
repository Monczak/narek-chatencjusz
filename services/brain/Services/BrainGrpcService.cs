using BrainService.Proto;
using BrainService.Services.Session;
using Grpc.Core;

namespace BrainService.Services;

public class BrainGrpcService(
    ILogger<BrainGrpcService> logger,
    NodeRegistryService nodeRegistry,
    CommandPublisher publisher,
    VoiceSessionService voiceSessionService,
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
        logger.LogInformation("Node {NodeId} requesting to join Guild {GuildId}, Channel {ChannelId}", request.NodeId, request.GuildId, request.ChannelId);

        var currentOwner = await nodeRegistry.GetNodeForGuildAsync(request.GuildId);
        var currentChannel = await nodeRegistry.GetChannelForGuildAsync(request.GuildId);

        var hasOwner = !string.IsNullOrEmpty(currentOwner);
        var sameGuild = currentOwner == request.NodeId;
        var sameChannel = currentChannel == request.ChannelId;

        switch (hasOwner, sameGuild, sameChannel)
        {
            case (true, true, true):
                return new JoinChannelResponse { Success = false, Message = "Already connected" };
            
            case (true, false, _):
                return new JoinChannelResponse { Success = false, Message = $"Guild is already handled by node {currentOwner}" };
            
            case (true, true, false) or (false, _, _):
                logger.LogInformation("Approved join for Node {NodeId} in Guild {GuildId}", request.NodeId, request.GuildId);

                var cmd = new BrainCommand
                {
                    Connect = new ConnectVoice
                    {
                        GuildId = request.GuildId,
                        ChannelId = request.ChannelId,
                        CorrelationId = request.CorrelationId
                    }
                };
                await publisher.PublishCommandAsync(request.NodeId, cmd);
                
                return new JoinChannelResponse
                {
                    Success = true,
                    Message = "Connection approved",
                };
        }
    }

    public override async Task<LeaveChannelResponse> LeaveChannel(LeaveChannelRequest request,
        ServerCallContext context)
    {
        var currentOwner = await nodeRegistry.GetNodeForGuildAsync(request.GuildId);
        var isOwner = currentOwner == request.NodeId;

        if (isOwner)
        {
            logger.LogInformation("Node {NodeId} leaving Guild {GuildId}", request.NodeId, request.GuildId);
        }

        if (isOwner || !string.IsNullOrEmpty(request.CorrelationId))
        {
            var cmd = new BrainCommand
            {
                Disconnect = new DisconnectVoice
                {
                    GuildId = request.GuildId, 
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

        if (isConnectedState && request.HasChannelId)
        {
            logger.LogInformation("State sync ({Reason}): Node {NodeId} confirmed connection in Guild {GuildId} Channel {ChannelId}", 
                request.Reason, request.NodeId, request.GuildId, request.ChannelId);
            
            await nodeRegistry.RegisterGuildConnectionAsync(request.GuildId, request.NodeId, request.ChannelId);
            await voiceSessionService.UpdateSessionChannelAsync(request.GuildId, request.ChannelId);
        }
        else if (isDisconnectedState)
        {
            var currentOwner = await nodeRegistry.GetNodeForGuildAsync(request.GuildId);
            var isOwner = currentOwner == request.NodeId;
            
            if (isOwner)
            {
                logger.LogInformation("State sync ({Reason}): Node {NodeId} confirmed disconnection from Guild {GuildId}", 
                    request.Reason, request.NodeId, request.GuildId);
                await nodeRegistry.UnregisterGuildConnectionAsync(request.GuildId);
            }
        }
        else
        {
            logger.LogWarning("Received ambiguous voice state notification from {NodeId} for Guild {GuildId}. Reason: {Reason}, HasChannel: {HasChannel}",
                request.NodeId, request.GuildId, request.Reason, request.HasChannelId);
        }

        return new VoiceStateAck { Success = true };
    }

    public override async Task<VoiceSessionEventAck> StreamVoiceSessionEvents(IAsyncStreamReader<VoiceSessionEvent> requestStream, ServerCallContext context)
    {
        var nodeId = context.RequestHeaders.GetValue("node_id");
        var activeGuilds = new HashSet<string>();
        
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(
            context.CancellationToken, 
            applicationLifetime.ApplicationStopping
        );

        try
        {
            await foreach (var voiceSessionEvent in requestStream.ReadAllAsync(cts.Token))
            {
                activeGuilds.Add(voiceSessionEvent.GuildId);
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
                foreach (var guildId in activeGuilds)
                {
                    logger.LogInformation("Marking session in Guild {GuildId} as unstable due to Node {NodeId} disconnect", guildId, nodeId);
                    await voiceSessionService.HandleNodeDisconnectAsync(guildId);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error reading voice event stream");
        }
        
        return new VoiceSessionEventAck { Success = true };
    }
}