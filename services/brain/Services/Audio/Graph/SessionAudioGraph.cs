using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using BrainService.Domain.Audio;
using BrainService.Domain.Session;
using BrainService.Services.Audio.Nodes;
using BrainService.Services.Audio.Transport;
using BrainService.Services.Audio.Vad;
using BrainService.Services.Configuration;

namespace BrainService.Services.Audio.Graph;

public sealed class SessionAudioGraph : IAsyncDisposable
{
    private readonly Guid _sessionId;
    private readonly ulong _guildId;
    private readonly UdpAudioServer _udpServer;
    private readonly BrainConfigService _configService;
    private readonly SileroVadModelService _vadModelService;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<SessionAudioGraph> _logger;

    private readonly MixerNode _mixer;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentBag<Task> _tasks = [];
    private readonly ConcurrentDictionary<ulong, UserPipeline> _pipelines = new();

    private CancellationToken _graphCt; // Set in RunAsync, used by CreatePipeline

    public SessionAudioGraph(
        Guid sessionId, ulong guildId,
        UdpAudioServer udpServer,
        BrainConfigService configService,
        SileroVadModelService vadModelService,
        ILoggerFactory loggerFactory)
    {
        _sessionId = sessionId;
        _guildId = guildId;

        _udpServer = udpServer;

        _configService = configService;
        _vadModelService = vadModelService;

        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<SessionAudioGraph>();

        _mixer = new MixerNode(loggerFactory.CreateLogger<MixerNode>());

        _graphCt = _cts.Token;
    }

    public async Task RunAsync(CancellationToken externalCt = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(externalCt, _cts.Token);
        _graphCt = linked.Token;

        _tasks.Add(RunOnDedicatedThread(() => _mixer.StartAsync(_graphCt), $"mixer-{_sessionId}"));

        _tasks.Add(Task.Factory.StartNew(
                () => OutputLoopAsync(_graphCt),
                _graphCt, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap()
        );

        _logger.LogInformation("Audio graph running for session {SessionId} guild {GuildId}", _sessionId, _guildId);

        try
        {
            await Task.Delay(Timeout.Infinite, _graphCt);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
    }

    // ReSharper disable once InconsistentNaming
    public void PushUserAudio(ulong userId, ReadOnlySpan<byte> pcm16BitStereo48k)
    {
        var pipeline = _pipelines.GetOrAdd(userId, CreatePipeline);
        pipeline.Push(userId, pcm16BitStereo48k);
    }

    private async Task OutputLoopAsync(CancellationToken ct)
    {
        var pcmBuf = new byte[AudioWireProtocol.PcmFrameSize];

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
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OutputLoop error for session {SessionId}", _sessionId);
        }
    }

    private UserPipeline CreatePipeline(ulong userId)
    {
        _logger.LogInformation("Creating pipeline for user {UserId} in session {SessionId}", userId, _sessionId);
        var pipeline = new UserPipeline(userId, _mixer, _configService, _vadModelService, _loggerFactory, _graphCt);
        pipeline.Start(_tasks);

        return pipeline;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try
        {
            await Task.WhenAll(_tasks).WaitAsync(TimeSpan.FromSeconds(5), _graphCt);
        }
        catch
        {
            // Best-effort
        }

        _cts.Dispose();
        _logger.LogInformation("Audio graph disposed for session {SessionId}", _sessionId);
    }

    private static Task RunOnDedicatedThread(Func<Task> work, string name)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        new Thread(() =>
        {
            try
            {
                work().GetAwaiter().GetResult();
                tcs.SetResult();
            }
            catch (OperationCanceledException)
            {
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = name,
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
