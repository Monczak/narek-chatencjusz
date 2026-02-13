using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using BrainService.Domain.Audio;
using BrainService.Services.Audio.Nodes;
using BrainService.Services.Audio.Transport;
using BrainService.Services.Configuration;

namespace BrainService.Services.Audio;

/// <summary>
/// Per-session DSP graph:
///
///   UDP inbound frame
///     → PushUserAudio (per-user Channel)
///       → [StereoToMono → Resample 48k→16k → VadGate → Resample 16k→48k → MonoToStereo]
///         → MixerNode (one input per user, dedicated timing thread)
///           → output loop → ConvertToPcm → UdpAudioServer.SendAudio
///
/// Per-user pipelines are created lazily on first received frame.
/// </summary>
public sealed class SessionAudioGraph : IAsyncDisposable
{
    private readonly Guid                       _sessionId;
    private readonly ulong                      _guildId;
    private readonly UdpAudioServer             _udpServer;
    private readonly BrainConfigService         _configService;
    private readonly SileroVadModelService      _vadModelService;
    private readonly ILoggerFactory             _loggerFactory;
    private readonly ILogger<SessionAudioGraph> _logger;

    private readonly MixerNode _mixer;
    private readonly CancellationTokenSource    _cts      = new();
    private readonly ConcurrentBag<Task>        _tasks    = [];
    private readonly ConcurrentDictionary<ulong, UserPipeline> _pipelines = new();

    private CancellationToken _graphCt; // set in RunAsync, used by CreatePipeline
    private const int SamplesPerFrame = 1920; // 48 kHz stereo 20 ms

    public SessionAudioGraph(
        Guid sessionId, ulong guildId,
        UdpAudioServer udpServer,
        BrainConfigService configService,
        SileroVadModelService vadModelService,
        ILoggerFactory loggerFactory)
    {
        _sessionId       = sessionId;
        _guildId         = guildId;
        _udpServer       = udpServer;
        _configService   = configService;
        _vadModelService = vadModelService;
        _loggerFactory   = loggerFactory;
        _logger          = loggerFactory.CreateLogger<SessionAudioGraph>();
        _mixer           = new MixerNode(loggerFactory.CreateLogger<MixerNode>());
        _graphCt         = _cts.Token;
    }

    // ── Entry point ───────────────────────────────────────────────────────────

    public async Task RunAsync(CancellationToken externalCt = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(externalCt, _cts.Token);
        _graphCt = linked.Token;

        // Mixer on dedicated AboveNormal thread — never yields to thread pool
        _tasks.Add(RunOnDedicatedThread(() => _mixer.StartAsync(_graphCt), $"mixer-{_sessionId}"));

        // Output: read mixed frames, send via UDP
        _tasks.Add(Task.Factory.StartNew(
            () => OutputLoopAsync(_graphCt),
            _graphCt, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap());

        _logger.LogInformation("Audio graph running for session {SessionId} guild {GuildId}", _sessionId, _guildId);

        // Block until cancelled — user pipeline tasks are added to _tasks dynamically
        // and are awaited in DisposeAsync.
        try { await Task.Delay(Timeout.Infinite, _graphCt); }
        catch (OperationCanceledException) { /* expected */ }
    }

    // ── Inbound from UDP server ───────────────────────────────────────────────

    /// <summary>Called on the UDP receive loop hot path — must be fast and non-blocking.</summary>
    public void PushUserAudio(ulong userId, ReadOnlySpan<byte> pcm16BitStereo48k)
    {
        var pipeline = _pipelines.GetOrAdd(userId, CreatePipeline);
        pipeline.Push(userId, pcm16BitStereo48k);
    }

    // ── Output loop ───────────────────────────────────────────────────────────

    private async Task OutputLoopAsync(CancellationToken ct)
    {
        var pcmBuf = new byte[AudioWireProtocol.PcmFrameSize]; // reused; single consumer

        try
        {
            await foreach (var frame in _mixer.Output.ReadAllAsync(ct))
            {
                ConvertFloatToPcm(frame.Samples.Span, pcmBuf);

                if (MemoryMarshal.TryGetArray(frame.Samples, out var seg) && seg.Array != null)
                    ArrayPool<float>.Shared.Return(seg.Array);

                _udpServer.SendAudio(_sessionId, _guildId, pcmBuf);
            }
        }
        catch (OperationCanceledException) { /* normal */ }
        catch (Exception ex) { _logger.LogError(ex, "OutputLoop error for session {SessionId}", _sessionId); }
    }

    // ── User pipeline factory ─────────────────────────────────────────────────

    private UserPipeline CreatePipeline(ulong userId)
    {
        _logger.LogInformation("Creating pipeline for user {UserId} in session {SessionId}", userId, _sessionId);
        var p = new UserPipeline(userId, _mixer, _configService, _vadModelService, _loggerFactory, _graphCt);
        p.Start(_tasks); // adds tasks to the shared ConcurrentBag
        return p;
    }

    // ── IAsyncDisposable ──────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try
        {
            // Wait for ALL tasks including dynamically added user pipeline tasks
            await Task.WhenAll(_tasks).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch { /* best-effort */ }
        _cts.Dispose();
        _logger.LogInformation("Audio graph disposed for session {SessionId}", _sessionId);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Task RunOnDedicatedThread(Func<Task> work, string name)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(() =>
        {
            try   { work().GetAwaiter().GetResult(); tcs.SetResult(); }
            catch (OperationCanceledException) { tcs.SetResult(); }
            catch (Exception ex)               { tcs.SetException(ex); }
        })
        {
            IsBackground = true,
            Priority     = ThreadPriority.AboveNormal,
            Name         = name,
        }.Start();
        return tcs.Task;
    }

    private static void ConvertFloatToPcm(ReadOnlySpan<float> samples, Span<byte> pcm)
    {
        var count = Math.Min(samples.Length, pcm.Length / 2);
        for (var i = 0; i < count; i++)
        {
            var s = (short)(Math.Clamp(samples[i], -1f, 1f) * 32767f);
            BinaryPrimitives.WriteInt16LittleEndian(pcm[(i * 2)..], s);
        }
    }
}

/// <summary>
/// Per-user DSP chain:
///   source channel → StereoToMono → Resample 48k→16k → VadGate → Resample 16k→48k → MonoToStereo → Mixer
/// </summary>
public sealed class UserPipeline
{
    private readonly Channel<AudioFrame> _source;
    private readonly IReadOnlyList<IAudioNode> _nodes;
    private readonly CancellationToken _ct;

    private const int SamplesPerFrame = 1920; // 48 kHz stereo

    public UserPipeline(
        ulong userId, MixerNode mixer,
        BrainConfigService config, SileroVadModelService vadModel,
        ILoggerFactory lf, CancellationToken ct)
    {
        _ct = ct;

        _source = Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(8)
        {
            FullMode     = BoundedChannelFullMode.DropOldest,
            SingleWriter = true,
            SingleReader = true,
        });

        // var stereoToMono = new ChannelConverterNode(
        //     _source.Reader, monoToStereo: false, lf.CreateLogger<ChannelConverterNode>());
        //
        // var resampleDown = new ResamplerNode(
        //     stereoToMono.Output, 48000, 16000, lf.CreateLogger<ResamplerNode>());
        //
        // var vad = new VadGateNode(
        //     resampleDown.Output,
        //     new SileroVadWrapper(vadModel.CreateSession()),
        //     config,
        //     lf.CreateLogger<VadGateNode>());
        //
        // var resampleUp = new ResamplerNode(
        //     vad.Output, 16000, 48000, lf.CreateLogger<ResamplerNode>());
        //
        // var monoToStereo = new ChannelConverterNode(
        //     resampleUp.Output, monoToStereo: true, lf.CreateLogger<ChannelConverterNode>());

        var reverb = new SchroederReverbNode(
            _source.Reader, lf);
        
        mixer.AddInput($"user_{userId}", reverb.Output);

        // _nodes = [stereoToMono, resampleDown, vad, resampleUp, monoToStereo];
        _nodes = [reverb];
    }

    public void Push(ulong userId, ReadOnlySpan<byte> pcm16BitStereo)
    {
        var floats = ArrayPool<float>.Shared.Rent(SamplesPerFrame);
        for (var i = 0; i < SamplesPerFrame; i++)
        {
            var s = BinaryPrimitives.ReadInt16LittleEndian(pcm16BitStereo[(i * 2)..]);
            floats[i] = s / 32768f;
        }

        var frame = new AudioFrame
        {
            Samples   = floats.AsMemory(0, SamplesPerFrame),
            UserId    = userId,
            Timestamp = DateTime.UtcNow,
        };

        if (!_source.Writer.TryWrite(frame))
        {
            // DropOldest is set, but guard anyway
            if (MemoryMarshal.TryGetArray(frame.Samples, out var seg) && seg.Array != null)
                ArrayPool<float>.Shared.Return(seg.Array);
        }
    }

    public void Start(ConcurrentBag<Task> taskBag)
    {
        foreach (var node in _nodes)
            taskBag.Add(Task.Factory.StartNew(
                () => node.StartAsync(_ct),
                _ct, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap());
    }
}
