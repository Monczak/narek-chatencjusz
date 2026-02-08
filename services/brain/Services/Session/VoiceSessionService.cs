using System.Text.Json;
using BrainService.Domain.Session;
using BrainService.Hubs;
using BrainService.Proto;
using Microsoft.AspNetCore.SignalR;
using RedLockNet;
using StackExchange.Redis;

namespace BrainService.Services.Session;

public class VoiceSessionService(
    IConnectionMultiplexer redis, 
    IDistributedLockFactory lockFactory, 
    ILogger<VoiceSessionService> logger,
    ILoggerFactory loggerFactory,
    NodeRegistryService nodeRegistry,
    IHubContext<DashboardHub> hubContext
) 
{
    private readonly IDatabase _db = redis.GetDatabase();

    public async Task<VoiceSessionState?> GetSessionStateAsync(string guildId)
    {
        var json = await _db.StringGetAsync($"session:{guildId}");
        return json.IsNullOrEmpty ? null : JsonSerializer.Deserialize<VoiceSessionState>(json.ToString());
    }

    public async Task<List<VoiceSessionState>> GetSessionStatesAsync()
    {
        var server = redis.GetServer(redis.GetEndPoints().First());
        var keys = server.Keys(pattern: $"session:*");

        var tasks = keys.Select(async key =>
        {
            var json = await _db.StringGetAsync(key);
            return json.IsNullOrEmpty ? null : JsonSerializer.Deserialize<VoiceSessionState>(json.ToString());
        });
        var results = await Task.WhenAll(tasks);
        return results.Where(x => x != null).OrderByDescending(x => x.LastUpdated).ToList();
    }

    public async Task HandleEventAsync(VoiceSessionEvent evt)
    {
        await ExecuteSessionTransactionAsync(evt.GuildId,
            async loadedState =>
            {
                var currentChannelId = await nodeRegistry.GetChannelForGuildAsync(evt.GuildId);
                
                if (evt.SessionUpdate?.ChangeType == SessionUpdate.Types.ChangeType.Started)
                {
                    // Check if we have an unstable session and the channels match - if so, resume the session
                    if (loadedState is { MachineState: VoiceSessionMachineState.Unstable } &&
                        loadedState.ChannelId == currentChannelId)
                    {
                        logger.LogInformation("Resuming unstable session for Guild {GuildId} in Channel {ChannelId}", evt.GuildId, currentChannelId);
                        return loadedState;
                    }
                    
                    logger.LogInformation("Starting new session for Guild {GuildId}", evt.GuildId);
                    return new VoiceSessionState { GuildId = evt.GuildId, ChannelId = currentChannelId };
                }
                
                var state = loadedState ?? new VoiceSessionState { GuildId = evt.GuildId };
                if (string.IsNullOrEmpty(state.ChannelId) && !string.IsNullOrEmpty(currentChannelId))
                {
                    state.ChannelId = currentChannelId;
                }

                return state;
            },
            machine =>
            {
                if (machine.State.MachineState == VoiceSessionMachineState.Unstable)
                {
                    machine.Recover();
                }
                machine.ProcessEvent(evt);
            },
            TimeSpan.FromSeconds(5)
        );
    }

    public async Task HandleNodeDisconnectAsync(string guildId)
    {
        await ExecuteSessionTransactionAsync(guildId, loadedState =>
            {
                if (loadedState == null || loadedState.MachineState == VoiceSessionMachineState.Ended)
                    return Task.FromResult<VoiceSessionState?>(null);
                return Task.FromResult(loadedState)!;
            },
            machine => machine.HandleNodeDisconnected(),
            TimeSpan.FromSeconds(2)
        );
    }

    public async Task UpdateSessionChannelAsync(string guildId, string channelId)
    {
        await ExecuteSessionTransactionAsync(guildId,
            loadedState =>
            {
                if (loadedState == null || loadedState.MachineState == VoiceSessionMachineState.Ended)
                    return Task.FromResult<VoiceSessionState?>(null);
                return Task.FromResult(loadedState)!;
            },
            machine => machine.UpdateChannel(channelId),
            TimeSpan.FromSeconds(2)
        );
    }

    private async Task ExecuteSessionTransactionAsync(
        string guildId,
        Func<VoiceSessionState?, Task<VoiceSessionState?>> stateResolver,
        Action<VoiceSessionStateMachine> stateProcessAction,
        TimeSpan timeout)
    {
        var lockKey = $"lock:session:{guildId}";
        var dataKey = $"session:{guildId}";

        await using var redLock = await lockFactory.CreateLockAsync(lockKey, timeout);
        if (!redLock.IsAcquired)
        {
            logger.LogWarning("Could not acquire lock for guild {GuildId}", guildId);
            return;
        }

        try
        {
            VoiceSessionState? state = null;
            var json = await _db.StringGetAsync(dataKey);

            if (!json.IsNullOrEmpty)
            {
                state = JsonSerializer.Deserialize<VoiceSessionState>(json.ToString());
            }

            state = await stateResolver(state);

            if (state == null) return;

            var machineLogger = loggerFactory.CreateLogger<VoiceSessionStateMachine>();
            var machine = new VoiceSessionStateMachine(state, machineLogger);

            stateProcessAction(machine);

            if (machine.IsDirty)
            {
                var newJson = JsonSerializer.Serialize(machine.State);
                await _db.StringSetAsync(dataKey, newJson, TimeSpan.FromHours(24));
                await hubContext.Clients.All.SendAsync("SessionUpdated", machine.State);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing session transaction for guild {GuildId}", guildId);
            throw;
        }
    }
}