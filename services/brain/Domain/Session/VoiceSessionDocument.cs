using MongoDB.Bson.Serialization.Attributes;

namespace BrainService.Domain.Session;

public class VoiceSessionDocument
{
    [BsonId] public string SessionId { get; set; } = null!;
    [BsonElement("guild_id")] public long GuildId { get; set; }
    [BsonElement("guild_name")] public string GuildName { get; set; }
    [BsonElement("channel_id")] public long? ChannelId { get; set; }
    [BsonElement("channel_name")] public string? ChannelName { get; set; }
    [BsonElement("node_id")] public string? NodeId { get; set; }
    [BsonElement("started_at")]  public DateTime StartedAt { get; set; }
    [BsonElement("ended_at")] public DateTime EndedAt { get; set; }
}