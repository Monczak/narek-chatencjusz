using BrainService.Domain.Tts;

namespace BrainService.Services.Tts;

public class TtsProviderFactory(SapiTtsProvider sapiProvider)
{
    public ITtsProvider GetProvider(TtsVoiceDefinition voice) => voice.ProviderType switch
    {
        TtsProviderType.Sapi => sapiProvider,
        TtsProviderType.Azure => throw new NotImplementedException("Azure TTS not yet implemented"),
        _ => throw new ArgumentOutOfRangeException(nameof(voice))
    };
}