using System.Collections.Concurrent;
using System.Text.Json;
using BrainService.Domain.Discord;
using BrainService.Domain.Llm;
using BrainService.Domain.Session;
using BrainService.Hubs;
using BrainService.Proto.Brain;
using BrainService.Services.Llm;
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
    VoiceSessionHistoryService historyService,
    GuildSettingsService settingsService,
    IHubContext<DashboardHub> hubContext
) 
{
    private readonly IDatabase _db = redis.GetDatabase();
    
    private sealed class SessionRuntimeState
    {
        public CancellationTokenSource? SilenceTimerCts;
        public CancellationTokenSource? GraceTimerCts;
    
        public Queue<VoiceSessionEventDocument> PendingEvents { get; } = new();
        public DateTime LastLlmContextEventAt { get; set; } = DateTime.UtcNow;
    }

    private readonly ConcurrentDictionary<string, SessionRuntimeState> _runtimeStates = new();
    
    private ILlmOrchestrationTrigger? _orchestrator;
    
    public void SetOrchestrator(ILlmOrchestrationTrigger trigger) => _orchestrator = trigger;
    
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
    
    public IReadOnlyList<VoiceSessionEventDocument> DrainPendingEvents(string sessionId)
    {
        if (!_runtimeStates.TryGetValue(sessionId, out var runtime))
            return [];

        var result = new List<VoiceSessionEventDocument>();
        while (runtime.PendingEvents.TryDequeue(out var evt))
            result.Add(evt);

        return result;
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
        
        var resultingState = VoiceSessionMachineState.Unstarted;

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
                resultingState = machine.State.MachineState;
            },
            TimeSpan.FromSeconds(5),
            evt
        );
        
        if (evt.UserState != null && sessionId != null)
        {
            var runtime = _runtimeStates.GetOrAdd(sessionId, _ => new SessionRuntimeState());
            if (resultingState is VoiceSessionMachineState.Thinking or VoiceSessionMachineState.Speaking)
            {
                // Can't inject into context right now - queue it
                var pendingDoc = new VoiceSessionEventDocument
                {
                    SessionId = sessionId,
                    Type = evt.UserState.ChangeType == UserVoiceStateUpdate.Types.ChangeType.Joined
                        ? VoiceSessionEventType.UserJoined
                        : VoiceSessionEventType.UserLeft,
                    UserId = (long)evt.UserState.User.Id,
                    Data = new MongoDB.Bson.BsonDocument { { "display_name", evt.UserState.User.DisplayName } },
                };
                runtime.PendingEvents.Enqueue(pendingDoc);
            }
            else if (resultingState == VoiceSessionMachineState.Idle
                     && evt.UserState.ChangeType == UserVoiceStateUpdate.Types.ChangeType.Joined)
            {
                // User joined while idle - start grace timer
                var settings = await settingsService.GetSettingsAsync(evt.Guild.Id);
                StartGraceTimer(sessionId, evt.Guild.Id, settings.UserJoinGraceMs, runtime);
            }
        }
    }

    public async Task HandleNodeDisconnectAsync(GuildContext guild)
    {
        var sessionId = await nodeRegistry.GetSessionForGuildAsync(guild.Id);
        if (sessionId == null)
        {
            logger.LogWarning("No session found for guild {GuildId} during node disconnect", guild.Id);
            return;
        }
        
        CancelSessionTimers(sessionId);
        _orchestrator?.Cancel(sessionId);

        await ExecuteSessionTransactionAsync(sessionId,
            loadedState =>
            {
                if (loadedState == null || loadedState.MachineState == VoiceSessionMachineState.Ended)
                    return Task.FromResult<VoiceSessionState?>(null);
                return Task.FromResult(loadedState)!;
            },
            machine => machine.HandleNodeDisconnected(),
            TimeSpan.FromSeconds(2),
            null
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
            TimeSpan.FromSeconds(2),
            null
        );
    }

    public async Task UpdateUserSpeakingStatusAsync(string? sessionId, ulong guildId, ulong userId, bool isSpeaking)
    {
        sessionId = await ResolveSessionIdAsync(guildId, sessionId);
        if (sessionId == null) return;

        VoiceSessionMachineState resultingState = VoiceSessionMachineState.Unstarted;
        bool speakingNowEmpty = false;

        await ExecuteSessionTransactionAsync(sessionId,
            Task.FromResult,
            machine =>
            {
                speakingNowEmpty = machine.UpdateUserSpeaking(userId, isSpeaking);
                resultingState   = machine.State.MachineState;
            },
            TimeSpan.FromSeconds(1),
            null
        );

        var runtime = _runtimeStates.GetOrAdd(sessionId, _ => new SessionRuntimeState());

        if (isSpeaking)
        {
            // Cancel silence timer if running
            CancelTimer(ref runtime.SilenceTimerCts);

            // Speaking during Thinking → cancel LLM immediately
            if (resultingState == VoiceSessionMachineState.Listening
                && _orchestrator != null)
            {
                // State machine already transitioned Thinking → Listening via UserSpeechStarted
                _orchestrator.Cancel(sessionId);
            }

            // Speaking during Speaking → check interruption threshold
            // For now: always cancel and transition
            if (resultingState == VoiceSessionMachineState.Speaking)
            {
                _orchestrator?.Cancel(sessionId);
                await FireConversationTriggerAsync(sessionId, VoiceSessionMachineTrigger.UserInterrupted);
            }
        }
        else if (speakingNowEmpty && resultingState == VoiceSessionMachineState.Listening)
        {
            // Last speaker stopped → start silence timer
            var settings = await settingsService.GetSettingsAsync(guildId);
            StartSilenceTimer(sessionId, guildId, settings.SilenceThresholdMs, runtime);
        }
    }
    
    private async Task ExecuteSessionTransactionAsync(
        string? sessionId,
        Func<VoiceSessionState?, Task<VoiceSessionState?>> stateResolver,
        Action<VoiceSessionStateMachine> stateProcessAction,
        TimeSpan timeout,
        VoiceSessionEvent? sourceEvent)
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

                _ = WriteHistoryAsync(machine.State, sourceEvent);
                
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

    private async Task WriteHistoryAsync(VoiceSessionState state, VoiceSessionEvent? evt)
    {
        try
        {
            switch (state.MachineState)
            {
                case VoiceSessionMachineState.Idle when evt?.SessionUpdate?.ChangeType == SessionUpdate.Types.ChangeType.Started:
                    await historyService.EnsureSessionAsync(state);
                    break;

                case VoiceSessionMachineState.Ended:
                    await historyService.MarkSessionEndedAsync(state.SessionId);
                    break;
            }

            if (evt?.UserState != null)
            {
                var user = new User(evt.UserState.User.Id, evt.UserState.User.DisplayName);
                switch (evt.UserState.ChangeType)
                {
                    case UserVoiceStateUpdate.Types.ChangeType.Joined:
                        await historyService.AppendUserJoinedAsync(state.SessionId, user.UserId, user.DisplayName);
                        break;
                    case UserVoiceStateUpdate.Types.ChangeType.Left:
                        await historyService.AppendUserLeftAsync(state.SessionId, user.UserId, user.DisplayName);
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to write history for session {SessionId}", state.SessionId);
        }
    }
    
    public async Task FireConversationTriggerAsync(string sessionId, VoiceSessionMachineTrigger trigger)
    {
        await ExecuteSessionTransactionAsync(sessionId,
            Task.FromResult,
            machine =>
            {
                switch (trigger)
                {
                    case VoiceSessionMachineTrigger.SilenceThresholdReached: machine.Fire(VoiceSessionMachineTrigger.SilenceThresholdReached); break;
                    case VoiceSessionMachineTrigger.RambleThresholdReached:  machine.Fire(VoiceSessionMachineTrigger.RambleThresholdReached);  break;
                    case VoiceSessionMachineTrigger.UserJoinedGraceExpired:  machine.Fire(VoiceSessionMachineTrigger.UserJoinedGraceExpired);  break;
                    case VoiceSessionMachineTrigger.LlmResponseStarted:      machine.Fire(VoiceSessionMachineTrigger.LlmResponseStarted);      break;
                    case VoiceSessionMachineTrigger.LlmResponseCompleted:    machine.Fire(VoiceSessionMachineTrigger.LlmResponseCompleted);    break;
                    case VoiceSessionMachineTrigger.LlmCanceled:             machine.Fire(VoiceSessionMachineTrigger.LlmCanceled);             break;
                    case VoiceSessionMachineTrigger.UserInterrupted:         machine.Fire(VoiceSessionMachineTrigger.UserInterrupted);         break;
                }
            },
            TimeSpan.FromSeconds(2),
            null
        );
    }
    
    private void StartSilenceTimer(string sessionId, ulong guildId, int delayMs, SessionRuntimeState runtime)
    {
        CancelTimer(ref runtime.SilenceTimerCts);
        var cts = new CancellationTokenSource();
        runtime.SilenceTimerCts = cts;

        _ = Task.Delay(delayMs, cts.Token).ContinueWith(async t =>
        {
            if (t.IsCanceled) return;
            try
            {
                await FireConversationTriggerAsync(sessionId, VoiceSessionMachineTrigger.SilenceThresholdReached);
                if (_orchestrator != null)
                    await _orchestrator.TriggerAsync(sessionId, guildId, LlmContextReason.UserSilence);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in silence timer callback for session {SessionId}", sessionId);
            }
        }, TaskScheduler.Default);
    }

    private void StartGraceTimer(string sessionId, ulong guildId, int delayMs, SessionRuntimeState runtime)
    {
        CancelTimer(ref runtime.GraceTimerCts);
        var cts = new CancellationTokenSource();
        runtime.GraceTimerCts = cts;

        _ = Task.Delay(delayMs, cts.Token).ContinueWith(async t =>
        {
            if (t.IsCanceled) return;
            try
            {
                await FireConversationTriggerAsync(sessionId, VoiceSessionMachineTrigger.UserJoinedGraceExpired);
                if (_orchestrator != null)
                    await _orchestrator.TriggerAsync(sessionId, guildId, LlmContextReason.UserJoined);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in grace timer callback for session {SessionId}", sessionId);
            }
        }, TaskScheduler.Default);
    }

    private static void CancelTimer(ref CancellationTokenSource? cts)
    {
        var existing = Interlocked.Exchange(ref cts, null);
        if (existing == null) return;
        existing.Cancel();
        existing.Dispose();
    }

    private void CancelSessionTimers(string sessionId)
    {
        if (!_runtimeStates.TryGetValue(sessionId, out var runtime)) return;
        CancelTimer(ref runtime.SilenceTimerCts);
        CancelTimer(ref runtime.GraceTimerCts);
    }
}
