namespace BrainService.Domain.Session;

public enum VoiceSessionMachineState
{
    // Lifecycle states
    Unstarted,
    Active,
    Ended,
    Unstable,
    
    // Conversation-level substates
    Idle,
    Listening,
    Thinking,
    Speaking,
}