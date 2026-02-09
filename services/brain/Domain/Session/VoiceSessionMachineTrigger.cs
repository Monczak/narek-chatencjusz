namespace BrainService.Domain.Session;

public enum VoiceSessionMachineTrigger
{
    UserJoined,
    UserLeft,
    SessionStarted,
    SessionEnded,
    SessionUnstable,
    NodeDisconnected,
    NodeReconnected,
}