using BrainService.Proto;
using Grpc.Core;
using StackExchange.Redis;

namespace BrainService.Services;

public class BrainGrpcService(
    ILogger<BrainGrpcService> logger,
    NodeRegistryService nodeRegistry,
    CommandPublisher publisher
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
                return new JoinChannelResponse { Success = true, Message = "Already connected" };
            
            case (true, false, _):
                return new JoinChannelResponse { Success = false, Message = $"Guild is already handled by node {currentOwner}" };
            
            case (true, true, false) or (false, _, _):
                await nodeRegistry.RegisterGuildConnectionAsync(request.GuildId, request.NodeId, request.ChannelId);
                logger.LogInformation("Approved join for Node {NodeId} in Guild {GuildId}", request.NodeId, request.GuildId);

                var cmd = new BrainCommand
                {
                    Connect = new ConnectVoice
                    {
                        GuildId = request.GuildId,
                        ChannelId = request.ChannelId,
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

        if (currentOwner == request.NodeId)
        {
            await nodeRegistry.UnregisterGuildConnectionAsync(request.GuildId);
            
            var cmd = new BrainCommand
            {
                Disconnect = new DisconnectVoice { GuildId = request.GuildId }
            };
            await publisher.PublishCommandAsync(request.NodeId, cmd);
            logger.LogInformation("Node {NodeId} leaving Guild {GuildId}", request.NodeId, request.GuildId);
        }
        
        return new LeaveChannelResponse { Success = true };
    }
}