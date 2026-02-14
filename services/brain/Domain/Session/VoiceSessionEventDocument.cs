using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace BrainService.Domain.Session;

public class VoiceSessionEventDocument
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    
    [BsonElement("session_id")] public string SessionId { get; set; } = null!;
    [BsonElement("timestamp")]  public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    
    [BsonElement("type")]
    [BsonRepresentation(BsonType.String)]
    public VoiceSessionEventType Type { get; set; }
    
    [BsonElement("user_id")]
    [BsonIgnoreIfNull]
    public long? UserId { get; set; }

    [BsonElement("data")] public BsonDocument Data { get; set; } = [];
}