using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace BrainService.Domain.Memory;

public class GuildMemoryDocument
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }
    
    [BsonElement("guild_id")] public ulong GuildId { get; set; }
    
    [BsonElement("key")] public required string Key { get; set; }
    [BsonElement("value")] public required string Value { get; set; }
    [BsonElement("updated_at")] public DateTime UpdatedAt { get; set; }
}