using System.Collections.Concurrent;
using BrainService.Domain.Llm;
using MongoDB.Driver;

namespace BrainService.Services.Llm;

public class GuildSettingsService
{
    private readonly IMongoCollection<GuildLlmSettings> _collection;
    private readonly ResolvedLlmSettings _defaults;
    private readonly ConcurrentDictionary<ulong, ResolvedLlmSettings> _cache = new();
    private readonly ILogger<GuildSettingsService> _logger;

    public GuildSettingsService(IMongoDatabase db, IConfiguration config, ILogger<GuildSettingsService> logger)
    {
        _logger = logger;
        _collection = db.GetCollection<GuildLlmSettings>("guild_llm_settings");

        var section = config.GetSection("Llm:Defaults");
            _defaults = new ResolvedLlmSettings(
            SystemPrompt:            section["SystemPrompt"] ?? DefaultPrompt,
            BotName:                 section["BotName"] ?? "Narek Narencjusz",
            ProviderType:            section["ProviderType"] != null ? Enum.Parse<LlmProviderType>(section["ProviderType"]!, ignoreCase: true) : LlmProviderType.LlamaCpp,
            LlmUrl:                  section["LlmUrl"] ?? "http://llm:7070",
            ModelName:               section["ModelName"],
            Temperature:             section.GetValue("Temperature", 0.8f),
            MaxTokens:               section.GetValue("MaxTokens", 1024),
            ContextWindow:           section.GetValue("ContextWindow", 8192),
            SilenceThresholdMs:      section.GetValue("SilenceThresholdMs", 1500),
            UserJoinGraceMs:         section.GetValue("UserJoinGraceMs", 3000),
            RambleModeEnabled:       section.GetValue("RambleModeEnabled", false),
            RambleThresholdMs:       section.GetValue("RambleThresholdMs", 90_000),
            RambleSystemHint:        section["RambleSystemHint"] ?? DefaultRambleHint,
            RambleMinResponseLength: section.GetValue("RambleMinResponseLength", 10),
            EnabledTools:            null
        );

        EnsureIndex();
    }

    public async Task<ResolvedLlmSettings> GetSettingsAsync(ulong guildId)
    {
        if (_cache.TryGetValue(guildId, out var settings))
        {
            return settings;
        }

        var @override = await _collection
            .Find(Builders<GuildLlmSettings>.Filter.Eq(s => s.GuildId, guildId))
            .FirstOrDefaultAsync();
        var resolved = Resolve(@override);
        _cache[guildId] = resolved;
        return resolved;
    }

    public async Task SaveSettingsAsync(GuildLlmSettings settings)
    {
        await _collection.ReplaceOneAsync(
            Builders<GuildLlmSettings>.Filter.Eq(s => s.GuildId, settings.GuildId),
            settings,
            new ReplaceOptions { IsUpsert = true }
        );
        _cache.TryRemove(settings.GuildId, out _);
        _logger.LogInformation("Saved LLM settings for {GuildId}", settings.GuildId);
    }
    
    public ResolvedLlmSettings GetDefaults() => _defaults;

    private ResolvedLlmSettings Resolve(GuildLlmSettings? g) => new(
        SystemPrompt:            g?.SystemPrompt            ?? _defaults.SystemPrompt,
        BotName:                 g?.BotName                 ?? _defaults.BotName,
        ProviderType:            g?.ProviderType            ?? _defaults.ProviderType,
        LlmUrl:                  g?.LlmUrl                  ?? _defaults.LlmUrl,
        ModelName:               g?.ModelName               ?? _defaults.ModelName,
        Temperature:             g?.Temperature             ?? _defaults.Temperature,
        MaxTokens:               g?.MaxTokens               ?? _defaults.MaxTokens,
        ContextWindow:           g?.ContextWindow           ?? _defaults.ContextWindow,
        SilenceThresholdMs:      g?.SilenceThresholdMs      ?? _defaults.SilenceThresholdMs,
        UserJoinGraceMs:         g?.UserJoinGraceMs         ?? _defaults.UserJoinGraceMs,
        RambleModeEnabled:       g?.RambleModeEnabled       ?? _defaults.RambleModeEnabled,
        RambleThresholdMs:       g?.RambleThresholdMs       ?? _defaults.RambleThresholdMs,
        RambleSystemHint:        g?.RambleSystemHint        ?? _defaults.RambleSystemHint,
        RambleMinResponseLength: g?.RambleMinResponseLength ?? _defaults.RambleMinResponseLength,
        EnabledTools:            g?.EnabledTools            ?? _defaults.EnabledTools
    );

    private void EnsureIndex()
    {
        _collection.Indexes.CreateOne(new CreateIndexModel<GuildLlmSettings>(
            Builders<GuildLlmSettings>.IndexKeys.Ascending(g => g.GuildId),
            new CreateIndexOptions { Unique = true, Background = true }
        ));
    }

    private const string DefaultPrompt =
        "You are {{bot_name}}, a companion in the {{guild_name}} Discord server." +
        "You participate in voice conversations naturally. Keep your responses concise - you're speaking aloud," +
        "not writing an essay. The current date and time is {{datetime}}.";

    private const string DefaultRambleHint =
        "[No one has spoken for a while. You may speek freely if you have something to say, or stay quiet.]";
}