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

    public async Task<VoiceSessionState?> GetSessionStateAsync(ulong guildId)
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
        await ExecuteSessionTransactionAsync(evt.Guild,
            async loadedState =>
            {
                var registryChannelId = await nodeRegistry.GetChannelForGuildAsync(evt.Guild.Id);
                
                if (evt.SessionUpdate?.ChangeType == SessionUpdate.Types.ChangeType.Started)
                {
                    // Check if we have an unstable session and the channels match - if so, resume the session
                    var eventChannel = evt.SessionUpdate.Channel;
                    var channelId = eventChannel?.Id ?? registryChannelId;
                    var channelName = eventChannel?.Name;
                    
                    if (loadedState is { MachineState: VoiceSessionMachineState.Unstable } &&
                        loadedState.ChannelId == channelId)
                    {
                        logger.LogInformation("Resuming unstable session for Guild {GuildId} in Channel {ChannelId}", evt.Guild.Id, channelId);
                        return loadedState;
                    }
                    
                    logger.LogInformation("Starting new session for Guild {GuildId} in Channel {ChannelId}", evt.Guild.Id, channelId);
                    return new VoiceSessionState
                    {
                        GuildId = evt.Guild.Id, 
                        GuildName = evt.Guild.Name, 
                        ChannelId = channelId, 
                        ChannelName = channelName
                    };
                }
                
                var state = loadedState ?? new VoiceSessionState { GuildId = evt.Guild.Id, GuildName = evt.Guild.Name };
                if (state.ChannelId.HasValue && !registryChannelId.HasValue)
                {
                    state.ChannelId = registryChannelId;
                    state.ChannelName = null;
                }

                return state;
            },
            machine =>
            {
                if (machine.State.MachineState == VoiceSessionMachineState.Unstable 
                    && evt.SessionUpdate?.ChangeType == SessionUpdate.Types.ChangeType.Started)
                {
                    machine.Recover();
                }
                machine.ProcessEvent(evt);
            },
            TimeSpan.FromSeconds(5)
        );
    }

    public async Task HandleNodeDisconnectAsync(GuildContext guild)
    {
        await ExecuteSessionTransactionAsync(guild, loadedState =>
            {
                if (loadedState == null || loadedState.MachineState == VoiceSessionMachineState.Ended)
                    return Task.FromResult<VoiceSessionState?>(null);
                return Task.FromResult(loadedState)!;
            },
            machine => machine.HandleNodeDisconnected(),
            TimeSpan.FromSeconds(2)
        );
    }

    public async Task UpdateSessionChannelAsync(GuildContext guild, ChannelContext channel)
    {
        logger.LogInformation("Updating session channel for Guild {GuildId} - {Channel}", guild.Id, channel);
        await ExecuteSessionTransactionAsync(guild,
            loadedState =>
            {
                if (loadedState == null || loadedState.MachineState == VoiceSessionMachineState.Ended)
                    return Task.FromResult<VoiceSessionState?>(null);
                return Task.FromResult(loadedState)!;
            },
            machine => machine.UpdateChannel(channel),
            TimeSpan.FromSeconds(2)
        );
    }

    public async Task UpdateUserSpeakingStatusAsync(ulong guildId, ulong userId, bool isSpeaking)
    {
        await ExecuteSessionTransactionAsync(new GuildContext { Id = guildId },
            Task.FromResult,
            machine => machine.UpdateUserSpeaking(userId, isSpeaking),
            TimeSpan.FromSeconds(1)
        );
    }

    private async Task ExecuteSessionTransactionAsync(
        GuildContext guild,
        Func<VoiceSessionState?, Task<VoiceSessionState?>> stateResolver,
        Action<VoiceSessionStateMachine> stateProcessAction,
        TimeSpan timeout)
    {
        var lockKey = $"lock:session:{guild.Id}";
        var dataKey = $"session:{guild.Id}";

        await using var redLock = await lockFactory.CreateLockAsync(lockKey, timeout);
        if (!redLock.IsAcquired)
        {
            logger.LogWarning("Could not acquire lock for guild {GuildId}", guild.Id);
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
            logger.LogError(ex, "Error processing session transaction for guild {GuildId}", guild.Id);
            throw;
        }
    }
}