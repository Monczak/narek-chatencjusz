using BrainService.Domain.Discord;
using BrainService.Proto;
using Stateless;

namespace BrainService.Domain.Session;

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
        // TODO: Configure state machine once there are speech events to handle

        _stateMachine.Configure(VoiceSessionMachineState.Unstarted)
            .Permit(VoiceSessionMachineTrigger.SessionStarted, VoiceSessionMachineState.Idle);

        _stateMachine.Configure(VoiceSessionMachineState.Idle)
            .Permit(VoiceSessionMachineTrigger.SessionEnded, VoiceSessionMachineState.Ended)
            .Permit(VoiceSessionMachineTrigger.NodeDisconnected,  VoiceSessionMachineState.Unstable)
            .Ignore(VoiceSessionMachineTrigger.SessionStarted);

        _stateMachine.Configure(VoiceSessionMachineState.Ended)
            .Ignore(VoiceSessionMachineTrigger.SessionEnded);
        
        _stateMachine.Configure(VoiceSessionMachineState.Unstable)
            .Permit(VoiceSessionMachineTrigger.NodeReconnected, VoiceSessionMachineState.Idle)
            .Permit(VoiceSessionMachineTrigger.SessionEnded, VoiceSessionMachineState.Ended)
            .Ignore(VoiceSessionMachineTrigger.NodeDisconnected);
        
        _stateMachine.OnTransitioned(t =>
        {
            _logger.LogInformation("[StateMachine] Session in Guild {GuildId}: {Source} -> {Dest} ({Trigger})",
                State.GuildId, t.Source, t.Destination, t.Trigger);
            State.LastUpdated = DateTime.UtcNow;
            IsDirty = true;
        });
    }

    public void ProcessEvent(VoiceSessionEvent evt)
    {
        switch (evt.EventDataCase)
        {
            case VoiceSessionEvent.EventDataOneofCase.SessionUpdate:
                HandleSessionUpdate(evt.SessionUpdate);
                break;
            case VoiceSessionEvent.EventDataOneofCase.UserState:
                HandleUserStateUpdate(evt.UserState);
                break;
            case VoiceSessionEvent.EventDataOneofCase.UserSpeaking:
                HandleUserSpeakingUpdate(evt.UserSpeaking);
                break;
        }
    }

    public void UpdateChannel(string channelId)
    {
        if (State.ChannelId != channelId)
        {
            State.ChannelId = channelId;
            IsDirty = true;
        }
    }
    
    public void HandleNodeDisconnected()
    {
        Fire(VoiceSessionMachineTrigger.NodeDisconnected);
    }

    public void Recover()
    {
        Fire(VoiceSessionMachineTrigger.NodeReconnected);
    }

    private void HandleUserSpeakingUpdate(UserSpeakingUpdate update)
    {
        // TODO: Keep track of speaking users once VAD is implemented
    }

    private void HandleUserStateUpdate(UserVoiceStateUpdate update)
    {
        switch (update.ChangeType)
        {
            case UserVoiceStateUpdate.Types.ChangeType.Joined:
                if (State.Users.Add(new User(update.UserId, update.UserDisplayName))) IsDirty = true;
                break;
            case UserVoiceStateUpdate.Types.ChangeType.Left:
                if (State.Users.Remove(new User(update.UserId, update.UserDisplayName))) IsDirty = true;
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
            case SessionUpdate.Types.ChangeType.Ended:
                Fire(VoiceSessionMachineTrigger.SessionEnded);
                break;
        }
    }

    private void Fire(VoiceSessionMachineTrigger trigger)
    {
        if (_stateMachine.CanFire(trigger))
        {
            _stateMachine.Fire(trigger);
        }
    }
}