using BrainService.Domain.Discord;

namespace BrainService.Domain.Session;

public class VoiceSessionState
{
    public required string GuildId { get; init; }
    public required string GuildName { get; init; }
    public string? ChannelId { get; set; }
    public string? ChannelName { get; set; }
    public string? NodeId { get; set; }
    public VoiceSessionMachineState MachineState { get; set; } = VoiceSessionMachineState.Unstarted;
    public HashSet<User> Users { get; init; } = []; // Has to have init to be deserialized properly
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}
