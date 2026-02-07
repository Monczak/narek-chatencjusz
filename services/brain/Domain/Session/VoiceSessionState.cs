using BrainService.Domain.Discord;

namespace BrainService.Domain.Session;

public class VoiceSessionState
{
    public required string GuildId { get; init; }
    public string? ChannelId { get; set; }
    public VoiceSessionMachineState MachineState { get; set; } = VoiceSessionMachineState.Unstarted;
    public HashSet<User> Users { get; } = [];
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}
