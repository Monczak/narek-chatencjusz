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
    UserSpeakingDetector speakingDetector,
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

        var hasOwner = !string.IsNullOrEmpty(currentOwner);
        var sameGuild = currentOwner == request.NodeId;
        var sameChannel = currentChannelId == request.Channel.Id;
        switch (hasOwner, sameGuild, sameChannel)
        {
            case (true, true, true):
                return new JoinChannelResponse { Success = false, Message = "Already connected" };
            
            case (true, false, _):
                return new JoinChannelResponse { Success = false, Message = $"Guild is already handled by node {currentOwner}" };
            
            case (true, true, false) or (false, _, _):
                logger.LogInformation("Approved join for Node {NodeId} in Guild {GuildId}", request.NodeId, request.Guild.Id);

                var cmd = new BrainCommand
                {
                    Connect = new ConnectVoice
                    {
                        Guild = request.Guild,
                        Channel = request.Channel,
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
        var currentOwner = await nodeRegistry.GetNodeForGuildAsync(request.Guild.Id);
        var isOwner = currentOwner == request.NodeId;

        if (isOwner)
        {
            logger.LogInformation("Node {NodeId} leaving Guild {GuildId}", request.NodeId, request.Guild);
        }

        if (isOwner || !string.IsNullOrEmpty(request.CorrelationId))
        {
            var cmd = new BrainCommand
            {
                Disconnect = new DisconnectVoice
                {
                    Guild = request.Guild, 
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
            logger.LogInformation("State sync ({Reason}): Node {NodeId} confirmed connection in Guild {Guild} Channel {Channel}", 
                request.Reason, request.NodeId, request.Guild, request.Channel);
            
            await nodeRegistry.RegisterGuildConnectionAsync(request.Guild, request.NodeId, request.Channel);
            await voiceSessionService.UpdateSessionChannelAsync(request.Guild, request.Channel);
        }
        else if (isDisconnectedState)
        {
            var currentOwner = await nodeRegistry.GetNodeForGuildAsync(request.Guild.Id);
            var isOwner = currentOwner == request.NodeId;
            
            if (isOwner)
            {
                logger.LogInformation("State sync ({Reason}): Node {NodeId} confirmed disconnection from Guild {Guild}", 
                    request.Reason, request.NodeId, request.Guild);
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

    public override async Task StreamAudio(IAsyncStreamReader<UserAudioFrame> requestStream, IServerStreamWriter<AudioFrame> responseStream, ServerCallContext context)
    {
        var nodeId = context.RequestHeaders.GetValue("node_id");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(
            context.CancellationToken, 
            applicationLifetime.ApplicationStopping
        );
        
        logger.LogInformation("Accepted audio stream from Node {NodeId}", nodeId);

        try
        {
            await foreach (var frame in requestStream.ReadAllAsync(cts.Token))
            {
                // TODO: Forward frames to the mixer
                speakingDetector.ProcessFrame(frame);
                logger.LogInformation("[AudioStream] {Timestamp} - User {UserId} speaking ({Prob:F1}%)", frame.Timestamp, frame.UserId, frame.SpeechProbability * 100);
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Audio stream from Node {NodeId} canceled", nodeId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error reading audio stream from Node {NodeId}", nodeId);
        }
        finally
        {
            logger.LogInformation("Audio stream from Node {NodeId} finished", nodeId);
        }
    }
}