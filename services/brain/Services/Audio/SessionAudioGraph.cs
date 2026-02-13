using System.IO.Pipelines;
using BrainService.Domain.Audio;
using BrainService.Hubs;
using BrainService.Proto;
using BrainService.Services.Audio.Nodes;
using BrainService.Services.Configuration;
using BrainService.Services.Session;
using Grpc.Core;
using Microsoft.AspNetCore.SignalR;

namespace BrainService.Services.Audio;

public class SessionAudioGraph : IAsyncDisposable
{
    private readonly string _sessionId;
    private readonly CancellationTokenSource _internalCts = new();
    private readonly List<Task> _nodeTasks = [];
    private readonly ILogger<SessionAudioGraph> _logger;
    private readonly IHubContext<DashboardHub> _hub;
    
    private readonly record struct NamedNode(string Name, IAudioNode Node);
    private readonly List<NamedNode> _monitoredNodes = [];
    
    // Nodes
    private readonly BotSourceNode _botSource;
    private readonly ChannelConverterNode _stereoToMono;
    private readonly ResamplerNode _resampler48To16;
    private readonly VadGateNode _vadGate;
    private readonly MixerNode _mixer;
    private readonly BotSinkNode _botSink;
    private readonly ResamplerNode _testResampler16To48;
    private readonly ChannelConverterNode _testMonoToStereo;
    private readonly UserDemuxerNode _demuxer;
    
    public SessionAudioGraph(
        string sessionId,
        ulong guildId,
        PipeReader botInputReader,
        IServerStreamWriter<Proto.AudioFrame> botOutputStream,
        VoiceSessionService sessionService,
        BrainConfigService configService,
        SileroVadModelService vadModelService,
        IHubContext<DashboardHub> hub,
        ILoggerFactory loggerFactory)
    {
        _sessionId = sessionId;
        _hub = hub;
        _logger = loggerFactory.CreateLogger<SessionAudioGraph>();
        
        _logger.LogInformation("Building audio graph for session {SessionId}", sessionId);
        
        // Bot Source: Receives 48kHz stereo int16 PCM from bot, converts to float32
        _botSource = new BotSourceNode(
            botInputReader,
            sessionId,
            loggerFactory.CreateLogger<BotSourceNode>()
        );
        
        // Convert stereo to mono for VAD
        // _stereoToMono = new ChannelConverterNode(
        //     _botSource.Output,
        //     monoToStereo: false,
        //     loggerFactory.CreateLogger<ChannelConverterNode>()
        // );
        //
        // // Resampler: 48kHz mono -> 16kHz mono for VAD
        // _resampler48To16 = new ResamplerNode(
        //     _stereoToMono.Output,
        //     fromRate: 48000,
        //     toRate: 16000,
        //     loggerFactory.CreateLogger<ResamplerNode>()
        // );
        //
        // // VAD Gate: Silero VAD with hysteresis, only passes speaking frames
        // _vadGate = new VadGateNode(
        //     _resampler48To16.Output,
        //     sessionService,
        //     configService,
        //     vadModelService,
        //     sessionId,
        //     guildId,
        //     loggerFactory.CreateLogger<VadGateNode>()
        // );
        
        // Mixer: Combines multiple audio sources
        // Currently produces silence as no sources are connected
        // Future: Add TTS source, soundboard, etc.
        _mixer = new MixerNode(loggerFactory.CreateLogger<MixerNode>());
        // TODO: When TTS is added:
        // var ttsResampler = new ResamplerNode(ttsSource.Output, 24000, 48000, ...)
        // var monoToStereo = new ChannelConverterNode(ttsResampler.Output, true, ...)
        // _mixer.AddInput("tts", monoToStereo.Output);
        
        // --- TEST DSP ROUTING ---
        // _testResampler16To48 = new ResamplerNode(
        //     _vadGate.Output,
        //     fromRate: 16000,
        //     toRate: 48000,
        //     loggerFactory.CreateLogger<ResamplerNode>()
        // );
        //
        // _testMonoToStereo = new ChannelConverterNode(
        //     _testResampler16To48.Output,
        //     monoToStereo: true,
        //     loggerFactory.CreateLogger<ChannelConverterNode>()
        // );
        
        _demuxer = new UserDemuxerNode(
            _botSource.Output,
            onNewUserStream: (userId, stream) => 
            {
                _mixer.AddInput($"echo_user_{userId}", stream);
            },
            loggerFactory.CreateLogger<UserDemuxerNode>()
        );
        
        // Bot Sink: Converts float32 to int16 PCM, streams to bot at 20ms intervals
        _botSink = new BotSinkNode(
            _mixer.Output,
            botOutputStream,
            guildId,
            loggerFactory.CreateLogger<BotSinkNode>()
        );
        
        // Register nodes for monitoring (BotSink has no meaningful Output; skip it)
        _monitoredNodes.AddRange([
            new NamedNode("BotSource",         _botSource),
            // new NamedNode("StereoToMono",      _stereoToMono),
            // new NamedNode("Resampler48to16",   _resampler48To16),
            // new NamedNode("VadGate",           _vadGate),
            // new NamedNode("EchoResampler",     _testResampler16To48),
            // new NamedNode("EchoMonoToStereo",  _testMonoToStereo),
            new NamedNode("Demuxer",           _demuxer),
            new NamedNode("Mixer",             _mixer),
        ]);
        
        _logger.LogInformation("Audio graph built for session {SessionId}", sessionId);
    }
    
    public async Task RunAsync(CancellationToken externalToken = default)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(externalToken, _internalCts.Token);
        var ct = linkedCts.Token;
        _logger.LogInformation("Starting audio graph for session {SessionId}", _sessionId);
        
        try
        {
            _nodeTasks.Add(Task.Factory.StartNew(
                () => _botSource.StartAsync(ct),
                ct,
                TaskCreationOptions.LongRunning, // <--- Tells scheduler to oversubscribe if needed
                TaskScheduler.Default).Unwrap());
            
            // _nodeTasks.Add(Task.Run(() => _stereoToMono.StartAsync(ct), ct));
            // _nodeTasks.Add(Task.Run(() => _resampler48To16.StartAsync(ct), ct));
            // _nodeTasks.Add(Task.Run(() => _vadGate.StartAsync(ct), ct));
            
            // --- TEST DSP ROUTING ---
            // _nodeTasks.Add(Task.Run(() => _testResampler16To48.StartAsync(ct), ct));
            // _nodeTasks.Add(Task.Run(() => _testMonoToStereo.StartAsync(ct), ct));
            _nodeTasks.Add(Task.Run(() => _demuxer.StartAsync(ct), ct));
            // ------------------------
            
            _nodeTasks.Add(RunOnDedicatedThread(_mixer.StartAsync, ct, $"mixer-{_sessionId}"));
            
            _nodeTasks.Add(Task.Factory.StartNew(
                () => _botSink.StartAsync(ct),
                ct,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).Unwrap());
            
            // Start DSP metrics push to dashboard
            // _nodeTasks.Add(Task.Run(() => MetricsLoopAsync(ct), ct));
            
            // Wait for all nodes to complete
            await Task.WhenAll(_nodeTasks);
            
            _logger.LogInformation("Audio graph completed for session {SessionId}", _sessionId);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Audio graph cancelled for session {SessionId}", _sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Audio graph error for session {SessionId}", _sessionId);
            throw;
        }
    }
    
    private static Task RunOnDedicatedThread(
        Func<CancellationToken, Task> work,
        CancellationToken ct,
        string name,
        ThreadPriority priority = ThreadPriority.AboveNormal)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                work(ct).GetAwaiter().GetResult();
                tcs.SetResult();
            }
            catch (OperationCanceledException) { tcs.SetResult(); }
            catch (Exception ex) { tcs.SetException(ex); }
        })
        {
            IsBackground = true,
            Priority = priority,
            Name = name
        };
        thread.Start();
        return tcs.Task;
    }
    
    private async Task MetricsLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try 
                {
                    var nodeMetrics = _monitoredNodes
                        .Select(n => new DspNodeMetrics(n.Name, n.Node.QueueDepth))
                        .ToList();
                
                    var metrics = new DspSessionMetrics(_sessionId, nodeMetrics, _botSink.AverageLatencyMs, DateTime.UtcNow);
                    await _hub.Clients.All.SendAsync("DspMetricsUpdated", metrics, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Failed to push DSP metrics");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
    }
    
    public async ValueTask DisposeAsync()
    {
        _logger.LogInformation("Disposing audio graph for session {SessionId}", _sessionId);
        
        await _internalCts.CancelAsync();
        
        try
        {
            await Task.WhenAll(_nodeTasks).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException)
        {
            _logger.LogWarning(
                "Audio graph nodes did not stop within 2s for session {SessionId}", _sessionId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error waiting for nodes to complete");
        }
        
        _internalCts.Dispose();
        _logger.LogInformation("Audio graph disposed for session {SessionId}", _sessionId);
        
        GC.SuppressFinalize(this);
    }
}
