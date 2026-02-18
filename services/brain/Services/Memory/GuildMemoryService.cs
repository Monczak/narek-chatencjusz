using BrainService.Domain.Memory;
using MongoDB.Driver;

namespace BrainService.Services.Memory;

public class GuildMemoryService
{
    private readonly IMongoCollection<GuildMemoryDocument> _collection;
    private readonly ILogger<GuildMemoryService> _logger;

    public GuildMemoryService(IMongoDatabase db, ILogger<GuildMemoryService> logger)
    {
        _logger = logger;
        _collection = db.GetCollection<GuildMemoryDocument>("guild_memories");

        _collection.Indexes.CreateOneAsync(
            new CreateIndexModel<GuildMemoryDocument>(
                Builders<GuildMemoryDocument>.IndexKeys
                    .Ascending(d => d.GuildId)
                    .Ascending(d => d.Key),
                new CreateIndexOptions { Unique = true }
            )
        );
    }

    public async Task SetAsync(ulong guildId, string key, string value, CancellationToken ct = default)
    {
        await _collection.ReplaceOneAsync(
            Builders<GuildMemoryDocument>.Filter.And(
                Builders<GuildMemoryDocument>.Filter.Eq(d => d.GuildId, guildId),
                Builders<GuildMemoryDocument>.Filter.Eq(d => d.Key, key)
            ),
            new GuildMemoryDocument
            {
                GuildId = guildId,
                Key = key,
                Value = value,
                UpdatedAt = DateTime.UtcNow,
            },
            new ReplaceOptions { IsUpsert = true },
            ct
        );

        _logger.LogDebug("Guild {GuildId}: memory set [{Key}] = {Value}", guildId, key, value);
    }

    public async Task<string?> GetAsync(ulong guildId, string key, CancellationToken ct = default)
    {
        var doc = await _collection
            .Find(Builders<GuildMemoryDocument>.Filter.And(
                Builders<GuildMemoryDocument>.Filter.Eq(d => d.GuildId, guildId),
                Builders<GuildMemoryDocument>.Filter.Eq(d => d.Key, key)
            ))
            .FirstOrDefaultAsync(ct);

        return doc?.Value;
    }

    public async Task<IReadOnlyDictionary<string, string>> GetAllAsync(ulong guildId, CancellationToken ct = default)
    {
        var docs = await _collection
            .Find(Builders<GuildMemoryDocument>.Filter.Eq(d => d.GuildId, guildId))
            .ToListAsync(ct);

        return docs.ToDictionary(d => d.Key, d => d.Value);
    }
}
