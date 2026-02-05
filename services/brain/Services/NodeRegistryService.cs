using System.Text.Json;
using StackExchange.Redis;

namespace BrainService.Services;

public class NodeRegistryService(IConnectionMultiplexer redis, ILogger<NodeRegistryService> logger)
{
    private readonly IDatabase _db = redis.GetDatabase();
    public async Task<string?> GetNodeForGuildAsync(string guildId)
    {
        var nodeId = await _db.StringGetAsync($"guild:{guildId}:connection");
        return nodeId.IsNullOrEmpty ? null : nodeId.ToString();
    }

    public async Task<string?> GetChannelForGuildAsync(string guildId)
    {
        var channelId = await _db.StringGetAsync($"guild:{guildId}:channel");
        return channelId.IsNullOrEmpty ? null : channelId.ToString();
    }

    public async Task<string?> GetNodeAddressAsync(string nodeId)
    {
        var heartbeatJson = await _db.StringGetAsync($"node:{nodeId}:heartbeat");
        if (heartbeatJson.IsNullOrEmpty)
        {
            logger.LogWarning("Node {NodeId} found in registry but has no heartbeat (Zombie?)", nodeId);
            return null;
        }

        var doc = JsonDocument.Parse(heartbeatJson.ToString());
        if (doc.RootElement.TryGetProperty("ip", out var ip))
        {
            return $"{ip.GetString()}:5050";
        }

        return null;
    }

    public async Task RegisterGuildConnectionAsync(string guildId, string nodeId, string channelId)
    {
        await _db.StringSetAsync($"guild:{guildId}:connection", nodeId);
        await _db.StringSetAsync($"guild:{guildId}:channel", channelId);
    }

    public async Task UnregisterGuildConnectionAsync(string guildId)
    {
        await _db.KeyDeleteAsync($"guild:{guildId}:connection");
        await _db.KeyDeleteAsync($"guild:{guildId}:channel");
    }
}