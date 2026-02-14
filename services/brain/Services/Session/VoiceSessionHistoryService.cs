using BrainService.Domain.Asr;
using BrainService.Domain.Session;
using MongoDB.Bson;
using MongoDB.Driver;

namespace BrainService.Services.Session;

public class VoiceSessionHistoryService
{
    private readonly IMongoCollection<VoiceSessionDocument> _sessionCollection;
    private readonly IMongoCollection<VoiceSessionEventDocument> _eventCollection;
    private readonly ILogger<VoiceSessionHistoryService> _logger;
    
    public VoiceSessionHistoryService(IMongoDatabase db, ILogger<VoiceSessionHistoryService> logger)
    {
        _sessionCollection = db.GetCollection<VoiceSessionDocument>("voice_sessions");
        _eventCollection = db.GetCollection<VoiceSessionEventDocument>("voice_session_events");
        _logger = logger;
        
        EnsureIndexes();
    }

    public async Task EnsureSessionAsync(VoiceSessionState state)
    {
        try
        {
            var doc = new VoiceSessionDocument
            {
                SessionId = state.SessionId,
                GuildId = (long)state.GuildId,
                GuildName = state.GuildName,
                ChannelId = state.ChannelId.HasValue ? (long)state.ChannelId.Value : null,
                ChannelName = state.ChannelName,
                NodeId = state.NodeId,
                StartedAt = DateTime.UtcNow,
            };

            await _sessionCollection.ReplaceOneAsync(
                Builders<VoiceSessionDocument>.Filter.Eq(s => s.SessionId, doc.SessionId),
                doc,
                new ReplaceOptions { IsUpsert = true }
            );

            await AppendEventAsync(state.SessionId, VoiceSessionEventType.Started, null, new BsonDocument
            {
                { "guild_id", (long)state.GuildId },
                { "guild_name", state.GuildName },
                { "channel_id", state.ChannelId.HasValue ? (long)state.ChannelId.Value : null },
                { "channel_name", state.ChannelName },
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upsert session {SessionId}", state.SessionId);
        }
    }

    public async Task<List<VoiceSessionEventDocument>> GetEventsAsync(string sessionId, int limit = 200) =>
        await _eventCollection
            .Find(Builders<VoiceSessionEventDocument>.Filter.Eq(s => s.SessionId, sessionId))
            .Sort(Builders<VoiceSessionEventDocument>.Sort.Descending(s => s.Timestamp))
            .Limit(limit)
            .ToListAsync();

    public async Task<List<VoiceSessionDocument>> GetRecentSessionsAsync(ulong? guildId = null, int limit = 50)
    {
        var filter = guildId.HasValue
            ? Builders<VoiceSessionDocument>.Filter.Eq(s => s.GuildId, (long)guildId.Value)
            : Builders<VoiceSessionDocument>.Filter.Empty;
        
        return await _sessionCollection
            .Find(filter)
            .Sort(Builders<VoiceSessionDocument>.Sort.Descending(s => s.StartedAt))
            .Limit(limit)
            .ToListAsync();
    }

    public async Task AppendUserJoinedAsync(string sessionId, ulong userId, string displayName) =>
        await AppendEventAsync(sessionId, VoiceSessionEventType.UserJoined, userId, new BsonDocument
        {
            { "display_name", displayName },
        });

    public async Task AppendUserLeftAsync(string sessionId, ulong userId, string displayName) =>
        await AppendEventAsync(sessionId, VoiceSessionEventType.UserLeft, userId, new BsonDocument
        {
            { "display_name", displayName },
        });
    
    public async Task AppendTranscriptAsync(TranscriptResult transcript) =>
        await AppendEventAsync(transcript.SessionId, VoiceSessionEventType.Transcript, transcript.UserId, new BsonDocument
        {
            { "text", transcript.Text },
            { "confidence", transcript.Confidence },
            { "language", transcript.Language },
            { "ended_at", transcript.EndedAt },
        }, transcript.StartedAt);
    public async Task MarkSessionEndedAsync(string sessionId)
    {
        try
        {
            await _sessionCollection.UpdateOneAsync(
                Builders<VoiceSessionDocument>.Filter.Eq(s => s.SessionId, sessionId),
                Builders<VoiceSessionDocument>.Update.Set(s => s.EndedAt, DateTime.UtcNow)
            );

            await AppendEventAsync(sessionId, VoiceSessionEventType.Ended, null, []);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to mark session {SessionId} ended", sessionId);
        }
    }

    private async Task AppendEventAsync(
        string sessionId, 
        VoiceSessionEventType type,
        ulong? userId,
        BsonDocument data,
        DateTime? timestamp = null
    )
    {
        try
        {
            var evt = new VoiceSessionEventDocument
            {
                SessionId = sessionId,
                Timestamp = timestamp ?? DateTime.UtcNow,
                Type = type,
                UserId = userId.HasValue ? (long)userId.Value : null,
                Data = data,
            };
            await _eventCollection.InsertOneAsync(evt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to append event {Type} for session {SessionId}", type, sessionId);
        }
    }

    private void EnsureIndexes()
    {
        var eventIndex = new CreateIndexModel<VoiceSessionEventDocument>(
            Builders<VoiceSessionEventDocument>.IndexKeys
                .Ascending(e => e.SessionId)
                .Ascending(e => e.Timestamp),
            new CreateIndexOptions { Background = true }
        );
        _eventCollection.Indexes.CreateOne(eventIndex);

        var sessionIndex = new CreateIndexModel<VoiceSessionDocument>(
            Builders<VoiceSessionDocument>.IndexKeys
                .Ascending(s => s.GuildId)
                .Descending(s => s.StartedAt),
            new CreateIndexOptions { Background = true }
        );
        _sessionCollection.Indexes.CreateOne(sessionIndex);
    }
}