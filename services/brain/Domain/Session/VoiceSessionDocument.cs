using MongoDB.Bson.Serialization.Attributes;

namespace BrainService.Domain.Session;

public class VoiceSessionDocument
{
    [BsonId] public required string SessionId { get; init; } = null!;
    [BsonElement("guild_id")] public required long GuildId { get; init; }
    [BsonElement("guild_name")] public required string GuildName { get; set; }
    [BsonElement("channel_id")] public long? ChannelId { get; set; }
    [BsonElement("channel_name")] public string? ChannelName { get; set; }
    [BsonElement("node_id")] public string? NodeId { get; set; }
    [BsonElement("started_at")]  public DateTime StartedAt { get; set; }
    [BsonElement("ended_at")] public DateTime EndedAt { get; set; }
}