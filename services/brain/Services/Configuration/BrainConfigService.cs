using System.Text.Json;
using BrainService.Domain.Configuration;
using StackExchange.Redis;

namespace BrainService.Services.Configuration;

public class BrainConfigService
{
    private readonly IDatabase _db;
    private readonly ISubscriber _sub;
    
    private const string ConfigKey = "config:brain";
    private const string ChannelKey = "config:updates";

    private static RedisChannel ConfigChannel => new(ChannelKey, RedisChannel.PatternMode.Literal);
    
    public BrainConfig Current { get; private set; } = new();
    public event Action<BrainConfig>? ConfigChanged;

    public BrainConfigService(IConnectionMultiplexer redis)
    {
        _db = redis.GetDatabase();
        _sub = redis.GetSubscriber();

        Task.Run(LoadConfigAsync);
        
        _sub.Subscribe(ConfigChannel, (channel, message) =>
        {
            if (message.HasValue)
            {
                Current = JsonSerializer.Deserialize<BrainConfig>(message.ToString()) ?? new BrainConfig();
                ConfigChanged?.Invoke(Current);
            }
        });
    }

    public async Task LoadConfigAsync()
    {
        var json = await _db.StringGetAsync(ConfigKey);
        if (!json.IsNullOrEmpty)
        {
            Current = JsonSerializer.Deserialize<BrainConfig>(json.ToString()) ?? new BrainConfig();
        }
    }

    public async Task UpdateConfigAsync(BrainConfig newConfig)
    {
        Current = newConfig;
        var json = JsonSerializer.Serialize(newConfig);
        await _db.StringSetAsync(ConfigKey, json);
        await _sub.PublishAsync(ConfigChannel, json);
    }
}