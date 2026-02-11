using BrainService.Domain.Audio;
using BrainService.Proto;
using BrainService.Services.Audio.Nodes;
using BrainService.Services.Configuration;
using BrainService.Services.Session;
using Grpc.Core;

namespace BrainService.Services.Audio;

/// <summary>
/// Orchestrates a complete audio processing graph for a single voice session.
/// Graph: Bot Source -> Stereo->Mono -> Resampler 48->16 -> VAD Gate -> Mixer -> Bot Sink
/// Future: TTS source -> Resampler 24->48 -> Mono->Stereo -> Mixer
/// </summary>
public class SessionAudioGraph : IAsyncDisposable
{
    private readonly string _sessionId;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _nodeTasks = [];
    private readonly ILogger<SessionAudioGraph> _logger;
    
    // Nodes in the graph
    private readonly BotSourceNode _botSource;
    private readonly ChannelConverterNode _stereoToMono;
    private readonly ResamplerNode _resampler48to16;
    private readonly VadGateNode _vadGate;
    private readonly MixerNode _mixer;
    private readonly BotSinkNode _botSink;
    
    public SessionAudioGraph(
        string sessionId,
        ulong guildId,
        IAsyncStreamReader<UserAudioFrame> botInputStream,
        IServerStreamWriter<Proto.AudioFrame> botOutputStream,
        VoiceSessionService sessionService,
        BrainConfigService configService,
        SileroVadModelService vadModelService,
        ILoggerFactory loggerFactory)
    {
        _sessionId = sessionId;
        _logger = loggerFactory.CreateLogger<SessionAudioGraph>();
        
        // Build the audio graph
        _logger.LogInformation("Building audio graph for session {SessionId}", sessionId);
        
        // Bot Source: Receives 48kHz stereo int16 PCM from bot, converts to float32
        _botSource = new BotSourceNode(
            botInputStream,
            sessionId,
            loggerFactory.CreateLogger<BotSourceNode>()
        );
        
        // Convert stereo to mono for VAD
        _stereoToMono = new ChannelConverterNode(
            _botSource.Output,
            monoToStereo: false,
            loggerFactory.CreateLogger<ChannelConverterNode>()
        );
        
        // Resampler: 48kHz mono -> 16kHz mono for VAD
        _resampler48to16 = new ResamplerNode(
            _stereoToMono.Output,
            fromRate: 48000,
            toRate: 16000,
            loggerFactory.CreateLogger<ResamplerNode>()
        );
        
        // VAD Gate: Silero VAD with hysteresis, only passes speaking frames
        _vadGate = new VadGateNode(
            _resampler48to16.Output,
            sessionService,
            configService,
            vadModelService,
            sessionId,
            guildId,
            loggerFactory.CreateLogger<VadGateNode>()
        );
        
        // Mixer: Combines multiple audio sources
        // Currently produces silence as no sources are connected
        // Future: Add TTS source, soundboard, etc.
        _mixer = new MixerNode(loggerFactory.CreateLogger<MixerNode>());
        // TODO: When TTS is added:
        // var ttsResampler = new ResamplerNode(ttsSource.Output, 24000, 48000, ...)
        // var monoToStereo = new ChannelConverterNode(ttsResampler.Output, true, ...)
        // _mixer.AddInput("tts", monoToStereo.Output);
        
        // Bot Sink: Converts float32 to int16 PCM, streams to bot at 20ms intervals
        _botSink = new BotSinkNode(
            _mixer.Output,
            botOutputStream,
            guildId,
            loggerFactory.CreateLogger<BotSinkNode>()
        );
        
        _logger.LogInformation("Audio graph built for session {SessionId}", sessionId);
    }
    
    public async Task RunAsync()
    {
        var ct = _cts.Token;
        
        _logger.LogInformation("Starting audio graph for session {SessionId}", _sessionId);
        
        try
        {
            // Start all nodes concurrently
            _nodeTasks.Add(Task.Run(() => _botSource.StartAsync(ct), ct));
            _nodeTasks.Add(Task.Run(() => _stereoToMono.StartAsync(ct), ct));
            _nodeTasks.Add(Task.Run(() => _resampler48to16.StartAsync(ct), ct));
            _nodeTasks.Add(Task.Run(() => _vadGate.StartAsync(ct), ct));
            _nodeTasks.Add(Task.Run(() => _mixer.StartAsync(ct), ct));
            _nodeTasks.Add(Task.Run(() => _botSink.StartAsync(ct), ct));
            
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
    
    public async ValueTask DisposeAsync()
    {
        _logger.LogInformation("Disposing audio graph for session {SessionId}", _sessionId);
        
        _cts.Cancel();
        
        try
        {
            // Wait for all nodes to complete with timeout
            await Task.WhenAll(_nodeTasks).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Audio graph nodes did not complete within timeout");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error waiting for nodes to complete");
        }
        
        _cts.Dispose();
    }
}
