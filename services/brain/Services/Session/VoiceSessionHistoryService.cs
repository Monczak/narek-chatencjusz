using BrainService.Domain.Asr;
using BrainService.Domain.Llm;
using BrainService.Domain.Session;
using BrainService.Hubs;
using Microsoft.AspNetCore.SignalR;
using MongoDB.Bson;
using MongoDB.Driver;

namespace BrainService.Services.Session;

public class VoiceSessionHistoryService
{
    private readonly IMongoCollection<VoiceSessionDocument> _sessionCollection;
    private readonly IMongoCollection<VoiceSessionEventDocument> _eventCollection;
    private readonly ILogger<VoiceSessionHistoryService> _logger;
    private readonly IHubContext<DashboardHub> _hubContext;

    private static readonly HashSet<VoiceSessionEventType> LlmContextTypes =
    [
        VoiceSessionEventType.Transcript,
        VoiceSessionEventType.BotResponse,
        VoiceSessionEventType.UserJoined,
        VoiceSessionEventType.UserLeft,
        VoiceSessionEventType.SystemNote,
    ];
    
    public VoiceSessionHistoryService(IMongoDatabase db, IHubContext<DashboardHub> hubContext, ILogger<VoiceSessionHistoryService> logger)
    {
        _sessionCollection = db.GetCollection<VoiceSessionDocument>("voice_sessions");
        _eventCollection = db.GetCollection<VoiceSessionEventDocument>("voice_session_events");
        _hubContext = hubContext;
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
            .Sort(Builders<VoiceSessionEventDocument>.Sort.Ascending(e => e.Timestamp))
            .Limit(limit)
            .ToListAsync();

    public async Task<List<VoiceSessionEventDocument>> GetLlmContextEventsAsync(
        string sessionId, int limit = 500, CancellationToken ct = default) =>
        await _eventCollection
            .Find(Builders<VoiceSessionEventDocument>.Filter.And(
                Builders<VoiceSessionEventDocument>.Filter.Eq(e => e.SessionId, sessionId),
                Builders<VoiceSessionEventDocument>.Filter.In(e => e.Type, LlmContextTypes)
            ))
            .Sort(Builders<VoiceSessionEventDocument>.Sort.Ascending(e => e.Timestamp))
            .Limit(limit)
            .ToListAsync(ct);
    
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
    
    public async Task<List<(long GuildId, string GuildName)>> GetKnownGuildsAsync()
    {
        var sessions = await _sessionCollection
            .Find(Builders<VoiceSessionDocument>.Filter.Empty)
            .Sort(Builders<VoiceSessionDocument>.Sort.Descending(s => s.StartedAt))
            .Project(s => new { s.GuildId, s.GuildName })
            .ToListAsync();

        return sessions
            .DistinctBy(s => s.GuildId)
            .Select(s => (s.GuildId, s.GuildName))
            .ToList();
    }

    public async Task AppendUserJoinedAsync(string sessionId, ulong userId, string displayName)
    {
        await AppendEventAsync(sessionId, VoiceSessionEventType.UserJoined, userId, new BsonDocument
        {
            { "display_name", displayName },
        });   
    }

    public async Task AppendUserLeftAsync(string sessionId, ulong userId, string displayName)
    {
        await AppendEventAsync(sessionId, VoiceSessionEventType.UserLeft, userId, new BsonDocument
        {
            { "display_name", displayName },
        });
    }

    public async Task AppendTranscriptAsync(TranscriptResult transcript)
    {
        await AppendEventAsync(transcript.SessionId, VoiceSessionEventType.Transcript, transcript.UserId, new BsonDocument
        {
            { "text", transcript.Text },
            { "confidence", transcript.Confidence },
            { "language", transcript.Language },
            { "ended_at", transcript.EndedAt },
        }, transcript.StartedAt);
        
        await _hubContext.Clients.All.SendAsync("TranscriptReceived", transcript.SessionId);
    }
    
    public async Task<string> AppendBotResponseAsync(
        string sessionId,
        string content,
        bool isPartial,
        LlmFinishReason? finishReason = null,
        int generationMs = 0,
        IReadOnlyList<object>? toolCalls = null,
        string? existingEventId = null)
    {
        var data = new BsonDocument
        {
            { "content", content ?? string.Empty },
            { "is_partial", isPartial },
            { "finish_reason", finishReason.HasValue 
                ? BsonValue.Create(finishReason.Value.ToString().ToLowerInvariant()) 
                : BsonNull.Value },
            { "generation_ms", generationMs },
        };
        
        if (toolCalls is { Count: > 0 })
            data["tool_calls"] = new BsonArray(toolCalls.Select(x => x.ToBsonDocument()));

        if (existingEventId != null)
        {
            await _eventCollection.UpdateOneAsync(
                Builders<VoiceSessionEventDocument>.Filter.Eq(e => e.Id, existingEventId),
                Builders<VoiceSessionEventDocument>.Update
                    .Set(e => e.Data, data)
                    .Set(e => e.Timestamp, DateTime.UtcNow)
            );
            await _hubContext.Clients.All.SendAsync(
                isPartial ? "LlmResponseChunk" : "LlmResponseCompleted", sessionId);
            return existingEventId;
        }

        var evt = new VoiceSessionEventDocument
        {
            SessionId = sessionId,
            Type = VoiceSessionEventType.BotResponse,
            Data = data,
        };
        await _eventCollection.InsertOneAsync(evt);
        await _hubContext.Clients.All.SendAsync("LlmResponseChunk", sessionId);
        return evt.Id;
    }
    
    public async Task MarkBotResponseCanceledAsync(string sessionId, string eventId, string partialContent)
    {
        await _eventCollection.UpdateOneAsync(
            Builders<VoiceSessionEventDocument>.Filter.Eq(e => e.Id, eventId),
            Builders<VoiceSessionEventDocument>.Update
                .Set("Data.content", partialContent)
                .Set("Data.is_partial", true)
                .Set("Data.finish_reason", nameof(LlmFinishReason.Cancelled).ToLowerInvariant()) 
        );
        await _hubContext.Clients.All.SendAsync("LlmResponseCanceled", sessionId);
    }
    
    public async Task AppendSystemNoteAsync(string sessionId, string note) =>
        await AppendEventAsync(sessionId, VoiceSessionEventType.SystemNote, null, new BsonDocument
        {
            { "note", note },
        });
    
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