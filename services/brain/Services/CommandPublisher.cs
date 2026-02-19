using BrainService.Proto.Brain;
using Google.Protobuf;
using StackExchange.Redis;

namespace BrainService.Services;

public class CommandPublisher(IConnectionMultiplexer redis, ILogger<CommandPublisher> logger)
{
    private readonly IDatabase _db =  redis.GetDatabase();
    private readonly ISubscriber _sub = redis.GetSubscriber();
    
    private const string BroadcastChannelName = "node:broadcast:commands";
    
    public async Task PublishCommandAsync(string nodeId, BrainCommand command)
    {
        var channelPattern = $"node:{nodeId}:commands";
        var channel = new RedisChannel(channelPattern, RedisChannel.PatternMode.Literal);
        var bytes = command.ToByteArray();
        await _db.PublishAsync(channel, bytes);
        logger.LogInformation("Published {CommandType} to {Channel}",  command.CommandCase, channel);
    } 
    
    public async Task BroadcastCommandAsync(BrainCommand command)
    {
        var channel = new RedisChannel(BroadcastChannelName, RedisChannel.PatternMode.Literal);
        var bytes = command.ToByteArray();
        await _sub.PublishAsync(channel, bytes);
        logger.LogInformation("Broadcast {CommandType} to all nodes", command.CommandCase);
    }
    
}