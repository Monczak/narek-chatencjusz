using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace BrainService.Domain.Guild;

public enum LlmProviderType
{
    Ollama,
    OpenAi
}

[BsonIgnoreExtraElements]
public class GuildSettings
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
    
    public string? TtsVoiceId { get; set; }
    public int? InterruptThresholdMs { get; set; }
    public int? SoundboardDrainBufferMs { get; set; }
}

public class ResolvedGuildSettings
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
    public string? TtsVoiceId { get; set; }
    public int InterruptThresholdMs { get; set; }
    public int SoundboardDrainBufferMs { get; set; }
}
