using BrainService.Proto;
using Google.Protobuf;
using StackExchange.Redis;

namespace BrainService.Services;

public class CommandPublisher(IConnectionMultiplexer redis, ILogger<CommandPublisher> logger)
{
    private readonly IDatabase _db =  redis.GetDatabase();
    
    public async Task PublishCommandAsync(string nodeId, BrainCommand command)
    {
        var channelPattern = $"node:{nodeId}:commands";
        var channel = new RedisChannel(channelPattern, RedisChannel.PatternMode.Literal);
        var bytes = command.ToByteArray();
        await _db.PublishAsync(channel, bytes);
        logger.LogInformation("Published {CommandType} to {Channel}",  command.CommandCase, channel);
    } 
}