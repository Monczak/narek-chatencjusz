using System.Text.Json;
using BrainService.Domain.Configuration;
using BrainService.Proto.Brain;
using StackExchange.Redis;
using BotNodeConfig = BrainService.Domain.Configuration.BotNodeConfig;

namespace BrainService.Services.Configuration;

public class BotConfigService(
    IConnectionMultiplexer redis,
    CommandPublisher commandPublisher,
    ILogger<BotConfigService> logger)
{
    private readonly IDatabase _db = redis.GetDatabase();

    private const string ConfigKey = "config:bot";

    public BotNodeConfig Current { get; private set; } = new();

    public async Task LoadConfigAsync()
    {
        var json = await _db.StringGetAsync(ConfigKey);
        if (!json.IsNullOrEmpty)
        {
            Current = JsonSerializer.Deserialize<BotNodeConfig>(json.ToString()) ?? new BotNodeConfig();
            logger.LogInformation("Loaded bot config from Redis");
        }
    }

    public async Task UpdateConfigAsync(BotNodeConfig newConfig)
    {
        Current = newConfig;
        var json = JsonSerializer.Serialize(newConfig);
        await _db.StringSetAsync(ConfigKey, json);

        await commandPublisher.BroadcastCommandAsync(new BrainCommand
        {
            RefreshBotConfig = new RefreshBotConfig()
        });
    }
}
