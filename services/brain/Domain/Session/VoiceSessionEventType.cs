namespace BrainService.Domain.Session;

public enum VoiceSessionEventType
{
    Started,
    Ended,
    UserJoined,
    UserLeft,
    Transcript,
    BotResponse,
    SystemNote,
}