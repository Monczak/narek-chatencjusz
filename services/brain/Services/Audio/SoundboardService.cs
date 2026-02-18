using System.Collections.Concurrent;
using System.Diagnostics;
using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace BrainService.Services.Audio;

public class SoundboardService(IConfiguration config, ILogger<SoundboardService> logger)
{
    private readonly string _samplesDirectory = config.GetValue<string>("Soundboard:SamplesDirectory") ?? "/app/soundboard";
    private readonly int _maxDurationSeconds = config.GetValue("Soundboard:MaxSampleDurationSeconds", 30);
    
    private readonly ConcurrentDictionary<string, float[]> _cache = new(StringComparer.OrdinalIgnoreCase);
    
    private static readonly string[] SupportedByNAudio = [".wav", ".mp3", ".aiff", ".aif", ".ogg"];

    public async Task LoadAllAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_samplesDirectory))
        {
            logger.LogInformation("Soundboard directory {Dir} not found - no sounds loaded", _samplesDirectory);
            return;
        }

        var files = Directory.GetFiles(_samplesDirectory);
        logger.LogInformation("Soundboard: loading {Count} file(s) from {Dir}", files.Length, _samplesDirectory);

        foreach (var file in files)
        {
            if (ct.IsCancellationRequested) break;
            await LoadFileAsync(file);
        }

        logger.LogInformation("Soundboard: {Count} sound(s) loaded: {Names}",
            _cache.Count, string.Join(", ", _cache.Keys));
    }

    private async Task LoadFileAsync(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();

        try
        {
            float[] samples;
            if (SupportedByNAudio.Contains(ext))
                samples = DecodeWithNAudio(path, ext);
            else
                samples = await DecodeWithFfmpegAsync(path);

            var maxSamples = _maxDurationSeconds * 48000 * 2; // stereo
            if (samples.Length > maxSamples)
            {
                logger.LogWarning("Soundboard: '{Name}' truncated to {Sec}s", name, _maxDurationSeconds);
                samples = samples[..maxSamples];
            }

            _cache[name] = samples;
            logger.LogDebug("Soundboard: loaded '{Name}' ({Samples} samples = {Ms:F0}ms)",
                name, samples.Length, samples.Length / 2.0 / 48000.0 * 1000.0);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Soundboard: failed to load '{Path}'", path);
        }
    }

    private static float[] DecodeWithNAudio(string path, string ext)
    {
        WaveStream reader = ext == ".ogg"
            ? new VorbisWaveReader(path)
            : new AudioFileReader(path);

        using (reader)
        {
            var source = reader.ToSampleProvider();

            // Resample to 48kHz if needed
            if (source.WaveFormat.SampleRate != 48000)
                source = new WdlResamplingSampleProvider(source, 48000);

            // Convert to stereo if mono
            if (source.WaveFormat.Channels == 1)
                source = new MonoToStereoSampleProvider(source);

            var samples = new List<float>(48000 * 10 * 2);
            var buffer = new float[4096];
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                samples.AddRange(buffer.AsSpan(0, read));

            return samples.ToArray();
        }
    }

    private async Task<float[]> DecodeWithFfmpegAsync(string path)
    {
        // Decode any format via FFmpeg to raw f32le stereo 48kHz on stdout
        using var proc = new Process();
        proc.StartInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = $"-i \"{path}\" -f f32le -ar 48000 -ac 2 -",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        proc.Start();

        using var ms = new MemoryStream();
        await proc.StandardOutput.BaseStream.CopyToAsync(ms);
        await proc.WaitForExitAsync();

        if (proc.ExitCode != 0)
        {
            var err = await proc.StandardError.ReadToEndAsync();
            throw new InvalidOperationException($"FFmpeg failed (exit {proc.ExitCode}): {err}");
        }

        var bytes = ms.ToArray();
        var floats = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return floats;
    }

    public float[]? TryGetSamples(string name) => _cache.GetValueOrDefault(name);

    public IReadOnlyList<string> GetSoundNames() => _cache.Keys.OrderBy(k => k).ToList();
}
