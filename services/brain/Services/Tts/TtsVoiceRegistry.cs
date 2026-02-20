using BrainService.Domain.Tts;

namespace BrainService.Services.Tts;

public class TtsVoiceRegistry
{
    private readonly Dictionary<string, TtsVoiceDefinition> _voices;

    public IReadOnlyList<TtsVoiceDefinition> Voices { get; }
    public int InterruptThresholdMs { get; }
    public int SoundboardDrainBufferMs { get; }

    public TtsVoiceRegistry(IConfiguration config)
    {
        InterruptThresholdMs = config.GetValue("Tts:InterruptThresholdMs", 1000);
        SoundboardDrainBufferMs = config.GetValue("Tts:SoundboardDrainBufferMs", 500);

        // IConfiguration.Bind() does not support polymorphic types, so we bind to
        // a flat config record first and then convert to the correct subclass
        var flatConfigs = new List<TtsVoiceConfig>();
        config.GetSection("Tts:Voices").Bind(flatConfigs);

        var defs = flatConfigs
            .Select(ToVoiceDefinition)
            .ToList();

        Voices = defs.AsReadOnly();
        _voices = defs.ToDictionary(v => v.VoiceId, StringComparer.OrdinalIgnoreCase);
    }

    public TtsVoiceDefinition? GetVoice(string? voiceId)
    {
        if (string.IsNullOrEmpty(voiceId)) return null;
        return _voices.GetValueOrDefault(voiceId);
    }

    private static TtsVoiceDefinition ToVoiceDefinition(TtsVoiceConfig cfg) => cfg.ProviderType switch
    {
        TtsProviderType.Sapi => new SapiVoiceDefinition
        {
            VoiceId     = cfg.VoiceId,
            DisplayName = cfg.DisplayName,
            ProviderUrl = cfg.ProviderUrl,
            VoiceName   = cfg.VoiceName,
            Rate        = (int)(cfg.Rate ?? 0),
            Volume      = cfg.Volume,
        },
        TtsProviderType.Azure => new AzureVoiceDefinition
        {
            VoiceId     = cfg.VoiceId,
            DisplayName = cfg.DisplayName,
            ProviderUrl = cfg.ProviderUrl,
            VoiceName   = cfg.VoiceName,
            Rate        = cfg.Rate,
            Pitch       = cfg.Pitch,
        },
        _ => throw new InvalidOperationException(
            $"Unknown TTS provider type '{cfg.ProviderType}' for voice '{cfg.VoiceId}'")
    };

    // Flat config record that accepts all provider-specific fields.
    // Fields not applicable to the chosen provider are ignored during conversion.
    // Rate is float? so it works for both SAPI (int, e.g. 0..10) and Azure (float, e.g. 1.2).
    private class TtsVoiceConfig
    {
        public string VoiceId { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public TtsProviderType ProviderType { get; set; }
        public string ProviderUrl { get; set; } = "";

        // Shared across providers
        public string? VoiceName { get; set; }

        // SAPI-specific
        public int Volume { get; set; } = 100; // 0-100

        // Shared: SAPI uses it as int (-10 to 10), Azure as float (relative rate)
        public float? Rate { get; set; }

        // Azure-specific
        public float? Pitch { get; set; }
    }
}
