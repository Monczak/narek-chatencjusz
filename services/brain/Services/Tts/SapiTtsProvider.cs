using System.Text;
using System.Text.Json;
using BrainService.Domain.Tts;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace BrainService.Services.Tts;

public sealed class SapiTtsProvider(IHttpClientFactory httpClientFactory, ILogger<SapiTtsProvider> logger) : ITtsProvider
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public async Task<TtsAudioResult> SynthesizeAsync(string text, TtsVoiceDefinition voice, CancellationToken ct)
    {
        var sapiVoice = (SapiVoiceDefinition)voice;

        var http = httpClientFactory.CreateClient();
        http.BaseAddress = new Uri(sapiVoice.ProviderUrl);

        var body = new SapiRequestBody(text, sapiVoice.VoiceName, sapiVoice.Rate, sapiVoice.Volume);
        using var content = new StringContent(
            JsonSerializer.Serialize(body, JsonOpts),
            Encoding.UTF8,
            "application/json"
        );

        logger.LogDebug("[TTS] Synthesising {Len} chars via SAPI ({Url})", text.Length, sapiVoice.ProviderUrl);
        var response = await http.PostAsync("/speak", content, ct);
        response.EnsureSuccessStatusCode();

        var wavBytes = await response.Content.ReadAsByteArrayAsync(ct);
        var samples = ConvertWavTo48kStereoFloat(wavBytes);
        var duration = TimeSpan.FromSeconds(samples.Length / 2.0 / 48000.0);

        logger.LogDebug("[TTS] {Bytes} bytes WAV -> {Samples} stereo samples ({Ms:F0} ms)",
            wavBytes.Length, samples.Length / 2, duration.TotalMilliseconds);

        return new TtsAudioResult(samples, duration);
    }

    // ReSharper disable once InconsistentNaming
    private float[] ConvertWavTo48kStereoFloat(byte[] wavBytes)
    {
        using var ms = new MemoryStream(wavBytes);
        using var reader = new WaveFileReader(ms);

        logger.LogDebug("[TTS] WAV format: {Rate} Hz, {Channels} ch, {Bits} bit",
            reader.WaveFormat.SampleRate, reader.WaveFormat.Channels, reader.WaveFormat.BitsPerSample);

        var source = reader.ToSampleProvider();

        if (source.WaveFormat.SampleRate != 48000)
            source = new WdlResamplingSampleProvider(source, 48000);

        if (source.WaveFormat.Channels == 1)
            source = new MonoToStereoSampleProvider(source);

        var samples = new List<float>(48000 * 5 * 2); // pre-allocate ~5 seconds stereo
        var buffer = new float[4096];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            samples.AddRange(buffer.AsSpan(0, read));

        return samples.ToArray();
    }

    private record SapiRequestBody(string Message, string? Voice, int Rate, int Volume);
}
