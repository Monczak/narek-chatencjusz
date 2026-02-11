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
    
    private async Task<string?> ResolveSessionIdAsync(ulong guildId, string? providedSessionId)
    {
        if (!string.IsNullOrEmpty(providedSessionId))
            return providedSessionId;
        
        // Fallback to guild lookup for backwards compatibility
        return await nodeRegistry.GetSessionForGuildAsync(guildId);
    }
    
    public async Task<VoiceSessionState?> GetSessionStateAsync(string sessionId)
    {
        var json = await _db.StringGetAsync($"session:{sessionId}");
        return json.IsNullOrEmpty ? null : JsonSerializer.Deserialize<VoiceSessionState>(json.ToString());
    }
    
    public async Task<VoiceSessionState?> GetSessionStateByGuildAsync(ulong guildId)
    {
        var sessionId = await nodeRegistry.GetSessionForGuildAsync(guildId);
        if (sessionId == null) return null;
        return await GetSessionStateAsync(sessionId);
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
        return results.OfType<VoiceSessionState>().OrderByDescending(x => x.LastUpdated).ToList();
    }

    public async Task HandleEventAsync(VoiceSessionEvent evt)
    {
        var sessionId = await ResolveSessionIdAsync(evt.Guild.Id, evt.SessionId);
        
        if (sessionId == null && evt.SessionUpdate?.ChangeType != SessionUpdate.Types.ChangeType.Started)
        {
            logger.LogWarning("No session ID found for event in guild {GuildId}, event type {EventType}", 
                evt.Guild.Id, evt.EventDataCase);
            return;
        }

        await ExecuteSessionTransactionAsync(sessionId,
            async loadedState =>
            {
                var registryChannelId = await nodeRegistry.GetChannelForGuildAsync(evt.Guild.Id);
                
                if (evt.SessionUpdate?.ChangeType == SessionUpdate.Types.ChangeType.Started)
                {
                    // Check if we have an unstable session and the channels match - if so, resume the session
                    var eventChannel = evt.SessionUpdate.Channel;
                    var channelId = eventChannel?.Id ?? registryChannelId;
                    var channelName = eventChannel?.Name;
                    
                    logger.LogInformation("{State}", loadedState?.MachineState);
                    if (loadedState is { MachineState: VoiceSessionMachineState.Unstable })
                    {
                        logger.LogInformation("Resuming unstable session {SessionId} for Guild {GuildId} in Channel {ChannelId}", 
                            loadedState.SessionId, evt.Guild.Id, channelId);
                        return loadedState;
                    }
                    
                    var newSessionId = sessionId ?? Guid.NewGuid().ToString();
                    logger.LogInformation("Starting new session {SessionId} for Guild {GuildId} in Channel {ChannelId}", 
                        newSessionId, evt.Guild.Id, channelId);
                    return new VoiceSessionState
                    {
                        SessionId = newSessionId,
                        GuildId = evt.Guild.Id, 
                        GuildName = evt.Guild.Name, 
                        ChannelId = channelId, 
                        ChannelName = channelName
                    };
                }
                
                var state = loadedState ?? new VoiceSessionState 
                { 
                    SessionId = sessionId!,
                    GuildId = evt.Guild.Id, 
                    GuildName = evt.Guild.Name 
                };
                
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
        var sessionId = await nodeRegistry.GetSessionForGuildAsync(guild.Id);
        if (sessionId == null)
        {
            logger.LogWarning("No session found for guild {GuildId} during node disconnect", guild.Id);
            return;
        }

        await ExecuteSessionTransactionAsync(sessionId,
            loadedState =>
            {
                if (loadedState == null || loadedState.MachineState == VoiceSessionMachineState.Ended)
                    return Task.FromResult<VoiceSessionState?>(null);
                return Task.FromResult(loadedState)!;
            },
            machine => machine.HandleNodeDisconnected(),
            TimeSpan.FromSeconds(2)
        );
    }

    public async Task UpdateSessionChannelAsync(GuildContext guild, ChannelContext channel, string? sessionId = null)
    {
        sessionId = await ResolveSessionIdAsync(guild.Id, sessionId);
        if (sessionId == null)
        {
            logger.LogWarning("No session found for guild {GuildId} during channel update", guild.Id);
            return;
        }

        logger.LogInformation("Updating session {SessionId} channel for Guild {GuildId} - {Channel}", sessionId, guild.Id, channel);
        await ExecuteSessionTransactionAsync(sessionId,
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

    public async Task UpdateUserSpeakingStatusAsync(string? sessionId, ulong guildId, ulong userId, bool isSpeaking)
    {
        sessionId = await ResolveSessionIdAsync(guildId, sessionId);
        if (sessionId == null) return;

        await ExecuteSessionTransactionAsync(sessionId,
            Task.FromResult,
            machine => machine.UpdateUserSpeaking(userId, isSpeaking),
            TimeSpan.FromSeconds(1)
        );
    }
    
    private async Task ExecuteSessionTransactionAsync(
        string? sessionId,
        Func<VoiceSessionState?, Task<VoiceSessionState?>> stateResolver,
        Action<VoiceSessionStateMachine> stateProcessAction,
        TimeSpan timeout)
    {
        // For new sessions, generate ID here if not provided
        sessionId ??= Guid.NewGuid().ToString();

        var lockKey = $"lock:session:{sessionId}";
        var dataKey = $"session:{sessionId}";

        await using var redLock = await lockFactory.CreateLockAsync(lockKey, timeout);
        if (!redLock.IsAcquired)
        {
            logger.LogWarning("Could not acquire lock for session {SessionId}", sessionId);
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
                
                if (machine.State.MachineState == VoiceSessionMachineState.Ended)
                {
                    await nodeRegistry.ClearLatestSessionForGuildAsync(machine.State.GuildId);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing session transaction for session {SessionId}", sessionId);
            throw;
        }
    }
}
