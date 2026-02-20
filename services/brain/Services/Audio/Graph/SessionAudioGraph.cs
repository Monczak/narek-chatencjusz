using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using BrainService.Domain.Audio;
using BrainService.Domain.Session;
using BrainService.Hubs;
using BrainService.Services.Asr;
using BrainService.Services.Audio.Nodes;
using BrainService.Services.Audio.Transport;
using BrainService.Services.Audio.Vad;
using BrainService.Services.Configuration;
using BrainService.Services.Session;
using Microsoft.AspNetCore.SignalR;

namespace BrainService.Services.Audio.Graph;

public sealed class SessionAudioGraph : IAsyncDisposable
{
    private readonly Guid _sessionId;
    private readonly ulong _guildId;
    private readonly UdpAudioServer _udpServer;
    private readonly BrainConfigService _configService;
    private readonly SileroVadModelService _vadModelService;
    private readonly VoiceSessionService _voiceSessionService;
    private readonly AsrGrpcClient _asrClient;
    private readonly VoiceSessionHistoryService _historyService;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<SessionAudioGraph> _logger;
    
    private readonly IHubContext<DashboardHub> _hubContext;

    public SoundboardNode Soundboard { get; } = new();
    public TtsNode Tts { get; } = new();
    
    private readonly MixerNode _mixer;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentBag<Task> _tasks = [];
    private readonly ConcurrentDictionary<ulong, UserPipeline> _pipelines = new();

    private CancellationToken _graphCt; // Set in RunAsync, used by CreatePipeline

    private double _averageLatencyMs;

    public SessionAudioGraph(
        Guid sessionId, 
        ulong guildId,
        UdpAudioServer udpServer,
        BrainConfigService configService,
        SileroVadModelService vadModelService,
        VoiceSessionService voiceSessionService,
        AsrGrpcClient asrClient,
        VoiceSessionHistoryService historyService,
        ILoggerFactory loggerFactory,
        IHubContext<DashboardHub> hubContext)
    {
        _sessionId = sessionId;
        _guildId = guildId;

        _udpServer = udpServer;

        _configService = configService;
        _vadModelService = vadModelService;
        _voiceSessionService = voiceSessionService;
        _asrClient = asrClient;
        _historyService = historyService;

        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<SessionAudioGraph>();
        
        _hubContext = hubContext;

        _mixer = new MixerNode(loggerFactory.CreateLogger<MixerNode>());
        
        _mixer.AddInput("bot_soundboard", Soundboard.Output);
        _mixer.AddInput("bot_tts", Tts.Output);

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
        
        _tasks.Add(Task.Run(() => MetricsLoopAsync(_graphCt), _graphCt));

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
                var latencyMs = (DateTime.UtcNow - frame.Timestamp).TotalMilliseconds;
                
                _averageLatencyMs = _averageLatencyMs == 0 
                    ? latencyMs 
                    : _averageLatencyMs * 0.9 + latencyMs * 0.1;
                
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

        var pipeline = new UserPipeline(
            _sessionId.ToString(),
            userId,
            _mixer,
            _configService,
            _vadModelService,
            _asrClient,
            _historyService,
            OnSpeakingStateChanged,
            _loggerFactory,
            _graphCt
        );
        
        pipeline.Start(_tasks);

        return pipeline;

        void OnSpeakingStateChanged(bool isSpeaking)
        {
            _ = _voiceSessionService.UpdateUserSpeakingStatusAsync(_sessionId.ToString(), _guildId, userId, isSpeaking);
        }
    }
    
    public DspSessionMetrics GetMetrics()
    {
        var nodeMetrics = new List<DspNodeMetrics>
        {
            new("Mixer", _mixer.Output.Count),
            new("TTS", Tts.QueueDepth),
        };

        foreach (var (userId, pipeline) in _pipelines)
        {
            var i = 1;
            foreach (var node in pipeline.Nodes)
            {
                // Uniquely identify each node per user
                nodeMetrics.Add(new DspNodeMetrics($"User_{userId}_{node.GetType().Name}_{i++}", node.QueueDepth));
            }
        }

        return new DspSessionMetrics(
            _sessionId.ToString(),
            nodeMetrics,
            _averageLatencyMs,
            DateTime.UtcNow
        );
    }
    
    private async Task MetricsLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
            
            while (await timer.WaitForNextTickAsync(ct))
            {
                var metrics = GetMetrics();
                await _hubContext.Clients.All.SendAsync("DspMetricsUpdated", metrics, cancellationToken: ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Metrics loop error for session {SessionId}", _sessionId);
        }
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
