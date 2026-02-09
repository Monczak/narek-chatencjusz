using System.Text.Json;
using BrainService.Proto;
using RedLockNet;
using StackExchange.Redis;

namespace BrainService.Services;

public class NodeRegistryService(
    IConnectionMultiplexer redis,
    IDistributedLockFactory lockFactory,
    ILogger<NodeRegistryService> logger)
{
    private readonly IDatabase _db = redis.GetDatabase();
    public async Task<string?> GetNodeForGuildAsync(ulong guildId)
    {
        var nodeId = await _db.StringGetAsync($"guild:{guildId}:connection");
        return nodeId.IsNullOrEmpty ? null : nodeId.ToString();
    }

    public async Task<ulong?> GetChannelForGuildAsync(ulong guildId)
    {
        var channel = await _db.StringGetAsync($"guild:{guildId}:channel");
        return channel.IsNullOrEmpty ? null : ulong.Parse(channel.ToString());
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

    public async Task RegisterGuildConnectionAsync(GuildContext guild, string nodeId, ChannelContext channel)
    {
        await using var redLock = await lockFactory.CreateLockAsync($"lock:guild:{guild.Id}", TimeSpan.FromSeconds(5));
        if (!redLock.IsAcquired)
        {
            logger.LogWarning("Could not acquire lock for guild {GuildId}", guild.Id);
            return;
        }
        
        await _db.StringSetAsync($"guild:{guild.Id}:connection", nodeId);
        await _db.StringSetAsync($"guild:{guild.Id}:channel", channel.Id);
    }

    public async Task UnregisterGuildConnectionAsync(GuildContext guild)
    {
        await _db.KeyDeleteAsync($"guild:{guild.Id}:connection");
        await _db.KeyDeleteAsync($"guild:{guild.Id}:channel");
    }

    public async Task CleanupStaleConnectionsAsync()
    {
        var server = redis.GetServer(redis.GetEndPoints().First());
        var guildKeys = server.Keys(pattern: "guild:*:connection");

        var cleanedCount = 0;

        foreach (var key in guildKeys)
        {
            try
            {
                var nodeId = await _db.StringGetAsync(key);
                if (nodeId.IsNullOrEmpty) continue;

                var heartbeatKey = $"node:{nodeId}:heartbeat";
                bool isAlive = await _db.KeyExistsAsync(heartbeatKey);

                if (!isAlive)
                {
                    await _db.KeyDeleteAsync(key);
                    logger.LogWarning("Cleaned up zombie connection mapped to dead node {NodeId} (key {Key})", nodeId,
                        key);
                    cleanedCount++;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing key {Key} during stale cleanup", key);
            }
        }

        if (cleanedCount > 0)
        {
            logger.LogInformation("Cleaned up {Count} stale connections", cleanedCount);
        }
    }
}