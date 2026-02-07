using System.Text.Json;
using BrainService.Domain.Session;
using BrainService.Proto;
using RedLockNet;
using StackExchange.Redis;

namespace BrainService.Services.Session;

public class VoiceSessionService(
    IConnectionMultiplexer redis, 
    IDistributedLockFactory lockFactory, 
    ILogger<VoiceSessionService> logger,
    ILoggerFactory loggerFactory
) 
{
    private readonly IDatabase _db = redis.GetDatabase();

    public async Task HandleEventAsync(VoiceSessionEvent evt)
    {
        var guildId = evt.GuildId;
        var lockKey = $"lock:session:{guildId}";
        var dataKey = $"session:{guildId}";
        
        await using var redLock = await lockFactory.CreateLockAsync(lockKey, TimeSpan.FromSeconds(5));
        if (!redLock.IsAcquired)
        {
            logger.LogWarning("Could not acquire lock for guild {GuildId} - skipping event", guildId);
            return;
        }

        try
        {
            VoiceSessionState state;

            if (evt.SessionUpdate?.ChangeType == SessionUpdate.Types.ChangeType.Started)
            {
                state = new VoiceSessionState {  GuildId = guildId };
            }
            else
            {
                var json = await _db.StringGetAsync(dataKey);

                if (json.IsNullOrEmpty)
                {
                    state = new VoiceSessionState { GuildId = guildId };
                }
                else
                {
                    state = JsonSerializer.Deserialize<VoiceSessionState>(json.ToString()) ??
                            new VoiceSessionState { GuildId = guildId };
                }
            }
            
            var machineLogger = loggerFactory.CreateLogger<VoiceSessionStateMachine>();
            var machine = new VoiceSessionStateMachine(state, machineLogger);

            machine.ProcessEvent(evt);

            if (machine.IsDirty)
            {
                var newJson = JsonSerializer.Serialize(machine.State);
                await _db.StringSetAsync(dataKey, newJson, TimeSpan.FromHours(24)); // Auto-expire stale sessions
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing session event for guild {GuildId}", guildId);
            throw;
        }
    }
}