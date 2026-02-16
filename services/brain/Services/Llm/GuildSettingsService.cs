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
        
        var promptsDir = config.GetValue<string>("Llm:PromptsDir");
        var systemPrompt = TryReadPromptFile(promptsDir, "system_prompt.txt", DefaultPrompt);
        var rambleHint = TryReadPromptFile(promptsDir, "ramble_hint.txt", DefaultRambleHint);

        var section = config.GetSection("Llm:Defaults");
            _defaults = new ResolvedLlmSettings(
            SystemPrompt:            section["SystemPrompt"] ?? systemPrompt,
            BotName:                 section["BotName"] ?? "Narek Narencjusz",
            ProviderType:            section["ProviderType"] != null ? Enum.Parse<LlmProviderType>(section["ProviderType"]!, ignoreCase: true) : LlmProviderType.LlamaCpp,
            ProviderUrl:             section["ProviderUrl"] ?? "http://llm:7070",
            ModelName:               section["ModelName"],
            Temperature:             section.GetValue("Temperature", 0.8f),
            MaxTokens:               section.GetValue("MaxTokens", 1024),
            ContextWindow:           section.GetValue("ContextWindow", 8192),
            SilenceThresholdMs:      section.GetValue("SilenceThresholdMs", 1500),
            UserJoinGraceMs:         section.GetValue("UserJoinGraceMs", 3000),
            RambleModeEnabled:       section.GetValue("RambleModeEnabled", false),
            RambleThresholdMs:       section.GetValue("RambleThresholdMs", 90_000),
            RambleSystemHint:        section["RambleSystemHint"] ?? rambleHint,
            RambleMinResponseLength: section.GetValue("RambleMinResponseLength", 10),
            EnabledTools:            null,
            TimeZone:                section["TimeZone"] ?? "UTC"
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
        ProviderUrl:                  g?.ProviderUrl                  ?? _defaults.ProviderUrl,
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
        EnabledTools:            g?.EnabledTools            ?? _defaults.EnabledTools,
        TimeZone:                g?.TimeZone                ?? _defaults.TimeZone
    );

    private void EnsureIndex()
    {
        _collection.Indexes.CreateOne(new CreateIndexModel<GuildLlmSettings>(
            Builders<GuildLlmSettings>.IndexKeys.Ascending(g => g.GuildId),
            new CreateIndexOptions { Unique = true, Background = true }
        ));
    }
    
    private string TryReadPromptFile(string? promptsDir, string fileName, string fallback)
    {
        if (string.IsNullOrEmpty(promptsDir))
            return fallback;

        var path = Path.Combine(promptsDir, fileName);
        if (!File.Exists(path))
        {
            _logger.LogWarning("Prompt file not found at {Path}, using built-in default", path);
            return fallback;
        }

        try
        {
            var content = File.ReadAllText(path).Trim();
            _logger.LogInformation("Loaded prompt from {Path}", path);
            return string.IsNullOrWhiteSpace(content) ? fallback : content;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read prompt file {Path}, using built-in default", path);
            return fallback;
        }
    }

    private const string DefaultPrompt =
        "You are {{bot_name}}, a companion in the {{guild_name}} Discord server. " +
        "You participate in voice conversations naturally. Keep your responses concise - you're speaking aloud, not writing an essay. " +
        "Your response should consist only of what you say - no user tag unlike the messages you receive. " +
        "The current date and time is {{datetime}}.";

    private const string DefaultRambleHint =
        "[No one has spoken for a while. You may speek freely if you have something to say, or stay quiet.]";
}