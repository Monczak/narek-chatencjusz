using System.Collections.Concurrent;
using System.Text.Json;
using BrainService.Domain.Discord;
using BrainService.Domain.Guild;
using BrainService.Domain.Llm;
using BrainService.Domain.Session;
using BrainService.Hubs;
using BrainService.Proto.Brain;
using BrainService.Services.Audio.Graph;
using BrainService.Services.Llm;
using BrainService.Services.Guild;
using BrainService.Services.Tts;
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
    AudioGraphFactory audioGraphFactory,
    IHubContext<DashboardHub> hubContext
) 
{
    private readonly IDatabase _db = redis.GetDatabase();

    private readonly ConcurrentDictionary<string, VoiceSessionRuntimeState> _runtimeStates = new();
    
    private ILlmOrchestrationTrigger? _orchestrator;
    private TtsResponseObserver? _ttsObserver;
    
    public void SetOrchestrator(ILlmOrchestrationTrigger trigger) => _orchestrator = trigger;
    public void SetTtsObserver(TtsResponseObserver observer) => _ttsObserver = observer;
    
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
            var runtime = _runtimeStates.GetOrAdd(sessionId, _ => new VoiceSessionRuntimeState());
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
                runtime.RambleTimer.Cancel();
                var settings = await settingsService.GetSettingsAsync(evt.Guild.Id);
                StartGraceTimer(sessionId, evt.Guild.Id, settings.UserJoinGraceMs, runtime);
            }
        }

        if (evt.SessionUpdate?.ChangeType == SessionUpdate.Types.ChangeType.Started
            && resultingState == VoiceSessionMachineState.Idle
            && sessionId != null)
        {
            await StartRambleTimerIfEnabledAsync(sessionId, evt.Guild.Id);
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

        var runtime = _runtimeStates.GetOrAdd(sessionId, _ => new VoiceSessionRuntimeState());
        runtime.GuildId = guildId;

        if (isSpeaking)
        {
            // User spoke - cancel any pending conversation timers
            runtime.SilenceTimer.Cancel();
            runtime.RambleTimer.Cancel();

            // Speaking during Thinking → cancel LLM immediately
            if (resultingState == VoiceSessionMachineState.Listening
                && _orchestrator != null)
            {
                // State machine already transitioned Thinking → Listening via UserSpeechStarted
                _orchestrator.Cancel(sessionId);
            }

            // Speaking during Speaking → check interruption threshold
            if (resultingState == VoiceSessionMachineState.Speaking)
                await HandleInterruptionAsync(sessionId);
        }
        else if (speakingNowEmpty && resultingState == VoiceSessionMachineState.Listening)
        {
            // Last speaker stopped → start silence timer
            var settings = await settingsService.GetSettingsAsync(guildId);
            StartSilenceTimer(sessionId, guildId, settings.SilenceThresholdMs, runtime);
        }
    }
    
    private async Task HandleInterruptionAsync(string sessionId)
    {
        if (!_runtimeStates.TryGetValue(sessionId, out var runtime)) return;

        var ttsNode = audioGraphFactory.TryGetTts(sessionId);
        var remainingMs = (ttsNode?.QueueDepth ?? 0) * 20; // each frame = 20ms

        if (remainingMs > 0)
        {
            var settings = await settingsService.GetSettingsAsync(runtime.GuildId);
            var thresholdMs = settings.InterruptThresholdMs;

            if (remainingMs < thresholdMs)
            {
                // Close to the end - not worth cutting off; let TTS finish naturally
                logger.LogDebug(
                    "[Interrupt] Session {SessionId} - {Ms}ms remaining < threshold {ThresholdMs}ms, ignoring interrupt",
                    sessionId, remainingMs, thresholdMs);
                return;
            }

            logger.LogInformation(
                "[Interrupt] Session {SessionId} - interrupting TTS ({Ms}ms remaining)", sessionId, remainingMs);
        }
        
        _ttsObserver?.CancelDrain(sessionId);
        ttsNode?.Flush();

        _orchestrator?.Cancel(sessionId);
        await FireConversationTriggerAsync(sessionId, VoiceSessionMachineTrigger.UserInterrupted);
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

            var stateBeforeMutation = state.MachineState;
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
                
                if (machine.State.MachineState == VoiceSessionMachineState.Idle
                    && stateBeforeMutation is VoiceSessionMachineState.Thinking
                        or VoiceSessionMachineState.Speaking)
                {
                    _ = OnTransitionedToIdleAsync(sessionId);
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
    
    public async Task<bool> FireConversationTriggerAsync(string sessionId, VoiceSessionMachineTrigger trigger)
    {
        var transitioned = false;
        await ExecuteSessionTransactionAsync(sessionId,
            Task.FromResult,
            machine =>
            {
                machine.Fire(trigger);
                transitioned = machine.IsDirty;
            },
            TimeSpan.FromSeconds(2),
            null
        );
        return transitioned;
    }

    private void StartSessionTimer(
        VoiceSessionTimer timer,
        string sessionId,
        ulong guildId,
        int delayMs,
        VoiceSessionMachineTrigger trigger,
        LlmContextReason reason)
    {
        timer.Start(delayMs, async () =>
        {
            try
            {
                var transitioned = await FireConversationTriggerAsync(sessionId, trigger);
                if (transitioned && _orchestrator != null)
                    await _orchestrator.TriggerAsync(sessionId, guildId, reason);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in {Trigger} timer callback for session {SessionId}", trigger, sessionId);
            }
        });
    }
    
    private async Task OnTransitionedToIdleAsync(string sessionId)
    {
        if (!_runtimeStates.TryGetValue(sessionId, out var runtime)) return;
        await StartRambleTimerIfEnabledAsync(sessionId, runtime.GuildId);
    }

    private void StartSilenceTimer(string sessionId, ulong guildId, int delayMs, VoiceSessionRuntimeState runtime) =>
        StartSessionTimer(runtime.SilenceTimer, sessionId, guildId, delayMs,
            VoiceSessionMachineTrigger.SilenceThresholdReached, LlmContextReason.UserSilence);

    private void StartRambleTimer(string sessionId, ulong guildId, int delayMs, VoiceSessionRuntimeState runtime) =>
        StartSessionTimer(runtime.RambleTimer, sessionId, guildId, delayMs,
            VoiceSessionMachineTrigger.RambleThresholdReached, LlmContextReason.Ramble);

    private void StartGraceTimer(string sessionId, ulong guildId, int delayMs, VoiceSessionRuntimeState runtime) =>
        StartSessionTimer(runtime.GraceTimer, sessionId, guildId, delayMs,
            VoiceSessionMachineTrigger.UserJoinedGraceExpired, LlmContextReason.UserJoined);

    public async Task StartRambleTimerIfEnabledAsync(string sessionId, ulong guildId)
    {
        var settings = await settingsService.GetSettingsAsync(guildId);
        if (!settings.RambleModeEnabled) return;

        var runtime = _runtimeStates.GetOrAdd(sessionId, _ => new VoiceSessionRuntimeState());
        runtime.GuildId = guildId;
        StartRambleTimer(sessionId, guildId, settings.RambleThresholdMs, runtime);
        logger.LogDebug("[Ramble] Session {SessionId} - ramble timer started ({ThresholdMs}ms)", sessionId, settings.RambleThresholdMs);
    }

    private void CancelSessionTimers(string sessionId)
    {
        if (!_runtimeStates.TryGetValue(sessionId, out var runtime)) return;
        runtime.SilenceTimer.Cancel();
        runtime.GraceTimer.Cancel();
        runtime.RambleTimer.Cancel();
    }
}
