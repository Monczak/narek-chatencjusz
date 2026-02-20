namespace BrainService.Domain.Tts;

public enum TtsProviderType
{
    Sapi,
    Azure,
}

public abstract class TtsVoiceDefinition
{
    public string VoiceId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string ProviderUrl { get; set; } = "";
    
    public abstract TtsProviderType ProviderType { get; }
}

public class SapiVoiceDefinition : TtsVoiceDefinition
{
    public override TtsProviderType ProviderType => TtsProviderType.Sapi;
    
    public string? VoiceName { get; set; }
    public int Rate { get; set; }
    public int Volume { get; set; } = 100;
}

public class AzureVoiceDefinition : TtsVoiceDefinition
{
    public override TtsProviderType ProviderType => TtsProviderType.Azure;
    
    public string? VoiceName { get; set; }
    public float? Rate { get; set; }
    public float? Pitch { get; set; }
}
