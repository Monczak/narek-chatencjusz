using BrainService.Domain.Tts;

namespace BrainService.Services.Tts;

public interface ITtsProvider
{
    Task<TtsAudioResult> SynthesizeAsync(string text, TtsVoiceDefinition voice, CancellationToken ct);
}