namespace BrainService.Services.Tts;

public record TtsAudioResult(float[] Samples, TimeSpan Duration);