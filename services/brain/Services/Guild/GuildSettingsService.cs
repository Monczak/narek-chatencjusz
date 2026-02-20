using System.Collections.Concurrent;
using BrainService.Domain.Guild;
using MongoDB.Driver;

namespace BrainService.Services.Guild;

public class GuildSettingsService
{
    private readonly IMongoCollection<GuildSettings> _collection;
    private readonly ResolvedGuildSettings _defaults;
    private readonly ConcurrentDictionary<ulong, ResolvedGuildSettings> _cache = new();
    private readonly ILogger<GuildSettingsService> _logger;

    public GuildSettingsService(IMongoDatabase db, IConfiguration config, ILogger<GuildSettingsService> logger)
    {
        _logger = logger;
        _collection = db.GetCollection<GuildSettings>("guild_settings");
        
        var systemPrompt = TryReadPromptFile(config.GetValue<string>("Llm:SystemPromptFile"), DefaultPrompt);
        var rambleHint = TryReadPromptFile(config.GetValue<string>("Llm:RambleHintFile"), DefaultRambleHint);
        var toolGuidance = TryReadPromptFile(config.GetValue<string>("Llm:ToolGuidanceFile"), DefaultToolGuidance);
        
        _defaults = GuildSettingsMapper.BuildDefaults(
            config.GetSection("Guild:Defaults"),
            systemPrompt,
            rambleHint,
            toolGuidance
        );

        EnsureIndex();
    }
    
    public async Task<ResolvedGuildSettings> GetSettingsAsync(ulong guildId)
    {
        if (_cache.TryGetValue(guildId, out var settings))
            return settings;

        var @override = await _collection
            .Find(Builders<GuildSettings>.Filter.Eq(s => s.GuildId, guildId))
            .FirstOrDefaultAsync();

        // (NEW)
        var resolved = GuildSettingsMapper.Resolve(@override, _defaults);
        // ---

        _cache[guildId] = resolved;
        return resolved;
    }
    
    public async Task<GuildSettings?> GetRawSettingsAsync(ulong guildId)
    {
        return await _collection
            .Find(Builders<GuildSettings>.Filter.Eq(s => s.GuildId, guildId))
            .FirstOrDefaultAsync();
    }

    public async Task<IReadOnlyList<GuildSettings>> GetAllRawSettingsAsync()
    {
        return await _collection
            .Find(Builders<GuildSettings>.Filter.Empty)
            .ToListAsync();
    }
    
    public async Task SaveSettingsAsync(GuildSettings settings)
    {
        await _collection.ReplaceOneAsync(
            Builders<GuildSettings>.Filter.Eq(s => s.GuildId, settings.GuildId),
            settings,
            new ReplaceOptions { IsUpsert = true }
        );
        _cache.TryRemove(settings.GuildId, out _);
        _logger.LogInformation("Saved LLM settings for {GuildId}", settings.GuildId);
    }
    
    public async Task DeleteSettingsAsync(ulong guildId)
    {
        await _collection.DeleteOneAsync(
            Builders<GuildSettings>.Filter.Eq(s => s.GuildId, guildId));
        _cache.TryRemove(guildId, out _);
        _logger.LogInformation("Deleted LLM settings for {GuildId}", guildId);
    }

    public ResolvedGuildSettings GetDefaults() => _defaults;

    private void EnsureIndex()
    {
        _collection.Indexes.CreateOne(
            new CreateIndexModel<GuildSettings>(
                Builders<GuildSettings>.IndexKeys.Ascending(s => s.GuildId),
                new CreateIndexOptions { Unique = true }));
    }

    private static string TryReadPromptFile(string? path, string fallback)
    {
        if (string.IsNullOrWhiteSpace(path)) return fallback;
        try   { return File.ReadAllText(path).Trim(); }
        catch { return fallback; }
    }
    
    private const string DefaultPrompt =
        "You are {{bot_name}}, a companion in the {{guild_name}} Discord server. " +
        "You participate in voice conversations naturally. Keep your responses concise - you're speaking aloud, not writing an essay. " +
        "Your response should consist only of what you say - no user tag unlike the messages you receive. " +
        "The current date and time is {{datetime}}.";

    private const string DefaultRambleHint =
        "[No one has spoken for a while. You may speak freely if you have something to say, or stay quiet.]";
    
    private const string DefaultToolGuidance =
        "You have access to tools you can invoke during your response. Only use a tool when it genuinely fits " +
        "the conversation - do not force tool usage. Prefer speaking naturally over invoking tools unnecessarily.";
}
