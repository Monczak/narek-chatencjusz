using System.Text;
using System.Text.Json;
using BrainService.Domain.Tts;
using NAudio.Dsp;
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

        var monoFloats = ReadAsMonoFloat(reader);

        float[] resampledFloats;
        if (reader.WaveFormat.SampleRate == 48000)
        {
            resampledFloats = monoFloats;
        }
        else
        {
            var resampler = new WdlResampler();
            resampler.SetMode(true, 0, false);
            resampler.SetFeedMode(true);
            resampler.SetRates(reader.WaveFormat.SampleRate, 48000);

            var ratio = 48000.0 / reader.WaveFormat.SampleRate;
            var estimatedOut = (int)(monoFloats.Length * ratio) + 64;
            resampledFloats = new float[estimatedOut];

            var inOffset = 0;
            var outOffset = 0;

            while (inOffset < monoFloats.Length)
            {
                var inAvail = monoFloats.Length - inOffset;
                var inConsumed = resampler.ResamplePrepare(inAvail, 1, out var inBuf, out var inBufOffset);
                Array.Copy(monoFloats, inOffset, inBuf, inBufOffset, inConsumed);
                inOffset += inConsumed;

                var outAvail = estimatedOut - outOffset;
                var outProduced = resampler.ResampleOut(resampledFloats, outOffset, inConsumed, outAvail, 1);
                outOffset += outProduced;
            }

            // Flush any samples held inside the resampler
            while (true)
            {
                var inConsumed = resampler.ResamplePrepare(0, 1, out var inBuf, out var inBufOffset);
                if (inConsumed == 0) break;
                Array.Clear(inBuf, inBufOffset, inConsumed);
                var outAvail = estimatedOut - outOffset;
                if (outAvail <= 0) break;
                var outProduced = resampler.ResampleOut(resampledFloats, outOffset, inConsumed, outAvail, 1);
                if (outProduced == 0) break;
                outOffset += outProduced;
            }

            resampledFloats = resampledFloats[..outOffset];
        }

        // Mono -> stereo interleave
        var stereo = new float[resampledFloats.Length * 2];
        for (var i = 0; i < resampledFloats.Length; i++)
        {
            stereo[i * 2]     = resampledFloats[i];
            stereo[i * 2 + 1] = resampledFloats[i];
        }

        return stereo;
    }

    private static float[] ReadAsMonoFloat(WaveFileReader reader)
    {
        var source = reader.ToSampleProvider();

        if (source.WaveFormat.Channels > 1)
            source = new StereoToMonoSampleProvider(source);

        var samples = new List<float>(reader.WaveFormat.SampleRate * 10);
        var buffer = new float[4096];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            samples.AddRange(buffer.AsSpan(0, read));

        return samples.ToArray();
    }

    private record SapiRequestBody(string Message, string? Voice, int Rate, int Volume);
}
