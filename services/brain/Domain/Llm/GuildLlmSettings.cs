using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace BrainService.Domain.Llm;

public enum LlmProviderType
{
    Ollama,
    OpenAi
}

[BsonIgnoreExtraElements]
public class GuildLlmSettings
{
    [BsonId]
    [BsonIgnoreIfDefault]
    public ObjectId Id { get; init; }
    
    public ulong GuildId { get; set; }

    public string? SystemPrompt { get; set; }
    public string? CustomInstructions { get; set; }
    public string? BotName { get; set; }
    
    public LlmProviderType? ProviderType { get; set; }
    public string? ProviderUrl { get; set; }
    public string? ModelName { get; set; }
    
    public float? Temperature { get; set; }
    public float? RepetitionPenalty {  get; set; }
    public int? MaxTokens { get; set; }
    public int? ContextWindow { get; set; }

    public int? SilenceThresholdMs { get; set; }
    public int? UserJoinGraceMs { get; set; }

    public bool? RambleModeEnabled { get; set; }
    public int? RambleThresholdMs { get; set; }
    public string? RambleSystemHint { get; set; }
    public int? RambleMinResponseLength { get; set; }
    
    public List<string>? EnabledTools { get; set; }
    public bool? ToolsEnabled { get; set; }
    
    public string? TimeZone { get; set; }
}

public class ResolvedLlmSettings
{
    public string SystemPrompt { get; set; } = "";
    public string? CustomInstructions { get; set; }
    public string BotName { get; set; } = "";
    public LlmProviderType ProviderType { get; set; }
    public string ProviderUrl { get; set; } = "";
    public string? ModelName { get; set; }
    public float Temperature { get; set; }
    public float RepetitionPenalty { get; set; }
    public int MaxTokens { get; set; }
    public int ContextWindow { get; set; }
    public int SilenceThresholdMs { get; set; }
    public int UserJoinGraceMs { get; set; }
    public bool RambleModeEnabled { get; set; }
    public int RambleThresholdMs { get; set; }
    public string RambleSystemHint { get; set; } = "";
    public int RambleMinResponseLength { get; set; }
    public IReadOnlyList<string>? EnabledTools { get; set; }
    public bool ToolsEnabled { get; set; }
    public string ToolGuidance { get; set; } = "";
    public string TimeZone { get; set; } = "UTC";
}
