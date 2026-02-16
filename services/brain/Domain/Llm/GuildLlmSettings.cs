using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace BrainService.Domain.Llm;

public enum LlmProviderType
{
    LlamaCpp,
    OpenAi
}

[BsonIgnoreExtraElements]
public class GuildLlmSettings
{
    [BsonId]
    public ObjectId Id { get; set; }
    
    public ulong GuildId { get; set; }

    public string? SystemPrompt { get; set; }
    public string? CustomInstructions { get; set; }
    public string? BotName { get; set; }
    
    public LlmProviderType? ProviderType { get; set; }

    public string? ProviderUrl { get; set; }
    public string? ModelName { get; set; }
    public float? Temperature { get; set; }
    public int? MaxTokens { get; set; }
    public int? ContextWindow { get; set; }

    public int? SilenceThresholdMs { get; set; }
    public int? UserJoinGraceMs { get; set; }

    public bool? RambleModeEnabled { get; set; }
    public int? RambleThresholdMs { get; set; }
    public string? RambleSystemHint { get; set; }
    public int? RambleMinResponseLength { get; set; }
    
    public List<string>? EnabledTools { get; set; } // All tools enabled if null
    
    public string? TimeZone { get; set; }
}

public record ResolvedLlmSettings(
    string SystemPrompt,
    string? CustomInstructions,
    string BotName,
    LlmProviderType ProviderType,
    string ProviderUrl,
    string? ModelName,
    float Temperature,
    int MaxTokens,
    int ContextWindow,
    int SilenceThresholdMs,
    int UserJoinGraceMs,
    bool RambleModeEnabled,
    int RambleThresholdMs,
    string RambleSystemHint,
    int RambleMinResponseLength,
    IReadOnlyList<string>? EnabledTools,
    string TimeZone
);
