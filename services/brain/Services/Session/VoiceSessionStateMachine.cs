using BrainService.Domain.Discord;
using BrainService.Domain.Session;
using BrainService.Proto.Brain;
using Stateless;

namespace BrainService.Services.Session;

public class VoiceSessionStateMachine
{
    public VoiceSessionState State { get; }
    public bool IsDirty { get; private set; }
    
    private readonly StateMachine<VoiceSessionMachineState, VoiceSessionMachineTrigger> _stateMachine;
    private readonly ILogger<VoiceSessionStateMachine> _logger;
    
    public VoiceSessionStateMachine(VoiceSessionState state, ILogger<VoiceSessionStateMachine> logger)
    {
        State = state;
        _logger = logger;

        _stateMachine = new StateMachine<VoiceSessionMachineState, VoiceSessionMachineTrigger>(
            () => State.MachineState,
            s =>
            {
                State.MachineState = s;
                IsDirty = true;
            }
        );
        ConfigureStateMachine();
    }

    private void ConfigureStateMachine()
    {
        // Lifecycle
        _stateMachine.Configure(VoiceSessionMachineState.Unstarted)
            .Permit(VoiceSessionMachineTrigger.SessionStarted, VoiceSessionMachineState.Idle);

        _stateMachine.Configure(VoiceSessionMachineState.Active)
            .Permit(VoiceSessionMachineTrigger.SessionEnded, VoiceSessionMachineState.Ended)
            .Permit(VoiceSessionMachineTrigger.NodeDisconnected,  VoiceSessionMachineState.Unstable)
            .Permit(VoiceSessionMachineTrigger.SessionUnstable,  VoiceSessionMachineState.Unstable)
            .Ignore(VoiceSessionMachineTrigger.SessionStarted)
            .Ignore(VoiceSessionMachineTrigger.UserJoined)
            .Ignore(VoiceSessionMachineTrigger.UserLeft);

        _stateMachine.Configure(VoiceSessionMachineState.Ended)
            .Ignore(VoiceSessionMachineTrigger.SessionEnded);
        
        _stateMachine.Configure(VoiceSessionMachineState.Unstable)
            .Permit(VoiceSessionMachineTrigger.NodeReconnected, VoiceSessionMachineState.Idle)
            .Ignore(VoiceSessionMachineTrigger.NodeDisconnected);
        
        // Conversation-level
        _stateMachine.Configure(VoiceSessionMachineState.Idle)
            .SubstateOf(VoiceSessionMachineState.Active)
            .Permit(VoiceSessionMachineTrigger.UserSpeechStarted, VoiceSessionMachineState.Listening)
            .Permit(VoiceSessionMachineTrigger.RambleThresholdReached, VoiceSessionMachineState.Thinking)
            .Permit(VoiceSessionMachineTrigger.UserJoinedGraceExpired, VoiceSessionMachineState.Thinking)
            .Ignore(VoiceSessionMachineTrigger.LlmResponseCompleted)
            .Ignore(VoiceSessionMachineTrigger.LlmCanceled);

        _stateMachine.Configure(VoiceSessionMachineState.Listening)
            .SubstateOf(VoiceSessionMachineState.Active)
            .Permit(VoiceSessionMachineTrigger.SilenceThresholdReached, VoiceSessionMachineState.Thinking)
            .Ignore(VoiceSessionMachineTrigger.UserSpeechStarted);

        _stateMachine.Configure(VoiceSessionMachineState.Thinking)
            .SubstateOf(VoiceSessionMachineState.Active)
            .Permit(VoiceSessionMachineTrigger.LlmResponseStarted, VoiceSessionMachineState.Speaking)
            .Permit(VoiceSessionMachineTrigger.LlmCanceled, VoiceSessionMachineState.Idle)
            .Permit(VoiceSessionMachineTrigger.UserSpeechStarted, VoiceSessionMachineState.Listening);

        _stateMachine.Configure(VoiceSessionMachineState.Speaking)
            .SubstateOf(VoiceSessionMachineState.Active)
            .Permit(VoiceSessionMachineTrigger.LlmResponseCompleted, VoiceSessionMachineState.Idle)
            .Permit(VoiceSessionMachineTrigger.LlmCanceled, VoiceSessionMachineState.Idle)
            .Permit(VoiceSessionMachineTrigger.UserInterrupted, VoiceSessionMachineState.Listening)
            .Ignore(VoiceSessionMachineTrigger.UserSpeechStarted);
        
        _stateMachine.OnTransitioned(t =>
        {
            _logger.LogInformation("[StateMachine] Session in Guild {GuildId}: {Source} -> {Dest} ({Trigger})",
                State.GuildId, t.Source, t.Destination, t.Trigger);
            State.LastUpdated = DateTime.UtcNow;
            IsDirty = true;
        });
    }
    
    public bool IsInState(VoiceSessionMachineState state) => _stateMachine.IsInState(state);

    public void ProcessEvent(VoiceSessionEvent evt)
    {
        if (State.NodeId != evt.NodeId)
        {
            State.NodeId = evt.NodeId;
            IsDirty = true;
        }
        
        switch (evt.EventDataCase)
        {
            case VoiceSessionEvent.EventDataOneofCase.SessionUpdate:
                HandleSessionUpdate(evt.SessionUpdate);
                break;
            case VoiceSessionEvent.EventDataOneofCase.UserState:
                HandleUserStateUpdate(evt.UserState);
                break;
            case VoiceSessionEvent.EventDataOneofCase.ChannelStateSnapshot:
                HandleChannelStateSnapshot(evt.ChannelStateSnapshot);
                break;
        }
    }

    public void UpdateChannel(ChannelContext channel)
    {
        if (State.ChannelId != channel.Id)
        {
            State.ChannelId = channel.Id;
            State.ChannelName = channel.Name;
            IsDirty = true;
        }
    }
    
    public bool UpdateUserSpeaking(ulong userId, bool isSpeaking)
    {
        bool changed;
        if (isSpeaking)
        {
            changed = State.SpeakingUsers.Add(userId);
            
            if (changed)
            {
                Fire(VoiceSessionMachineTrigger.UserSpeechStarted);
            }
        }
        else
        {
            changed = State.SpeakingUsers.Remove(userId);
        }

        if (changed)
        {
            State.LastUpdated = DateTime.UtcNow;
            IsDirty = true;
        }

        return State.SpeakingUsers.Count == 0;
    }
    
    public void HandleNodeDisconnected() => Fire(VoiceSessionMachineTrigger.NodeDisconnected);

    public void Recover() => Fire(VoiceSessionMachineTrigger.NodeReconnected);
    
    public void Fire(VoiceSessionMachineTrigger trigger)
    {
        if (_stateMachine.CanFire(trigger))
        {
            _stateMachine.Fire(trigger);
        }
    }

    private void HandleUserStateUpdate(UserVoiceStateUpdate update)
    {
        switch (update.ChangeType)
        {
            case UserVoiceStateUpdate.Types.ChangeType.Joined:
                if (State.Users.Add(new User(update.User.Id, update.User.DisplayName))) IsDirty = true;
                break;
            case UserVoiceStateUpdate.Types.ChangeType.Left:
                if (State.Users.RemoveWhere(u => u.UserId == update.User.Id) > 0) IsDirty = true;
                break;
        }
    }

    private void HandleSessionUpdate(SessionUpdate update)
    {
        switch (update.ChangeType)
        {
            case SessionUpdate.Types.ChangeType.Started:
                Fire(VoiceSessionMachineTrigger.SessionStarted);
                break;
            case SessionUpdate.Types.ChangeType.Unstable:
                Fire(VoiceSessionMachineTrigger.SessionUnstable);
                break;
            case SessionUpdate.Types.ChangeType.Ended:
                Fire(VoiceSessionMachineTrigger.SessionEnded);
                break;
        }
    }
    
    private void HandleChannelStateSnapshot(ChannelStateSnapshot snapshot)
    {
        State.Users.Clear();
        foreach (var user in snapshot.Users)
        {
            State.Users.Add(new User(user.Id, user.DisplayName));
        }

        IsDirty = true;

        if (State.ChannelId != snapshot.Channel.Id)
        {
            State.ChannelId = snapshot.Channel.Id;
            State.ChannelName = snapshot.Channel.Name;
        }
    }
}