using System.Collections.Concurrent;
using System.Runtime.InteropServices; // Required for CollectionsMarshal
using System.Threading.Channels;
using BrainService.Domain.Audio;
using BrainService.Services.Configuration;
using BrainService.Services.Session;

namespace BrainService.Services.Audio.Nodes;

public class VadGateNode : IAudioNode
{
    private readonly Channel<AudioFrame> _output;
    private readonly ChannelReader<AudioFrame> _input;
    private readonly Dictionary<ulong, (SileroVadNode vad, List<float> buffer)> _userVadNodes;
    private readonly VoiceSessionService _sessionService;
    private readonly BrainConfigService _configService;
    private readonly SileroVadModelService _vadModelService;
    private readonly string _sessionId;
    private readonly ulong _guildId;
    private readonly ILogger<VadGateNode> _logger;
    
    // Per-user state tracking
    private class UserVadState
    {
        public bool IsSpeaking { get; set; }
        public DateTime LastSpeakingFrame { get; set; }
        public DateTime LastFrameReceived { get; set; }
    }
    
    private readonly ConcurrentDictionary<ulong, UserVadState> _userStates = new();
    private readonly CancellationTokenSource _silenceDetectionCts = new();
    
    public ChannelReader<AudioFrame> Output => _output.Reader;
    
    // VAD expects exactly 512 samples at 16kHz mono
    private const int VadWindowSize = 512;
    
    public VadGateNode(
        ChannelReader<AudioFrame> input,
        VoiceSessionService sessionService,
        BrainConfigService configService,
        SileroVadModelService vadModelService,
        string sessionId,
        ulong guildId,
        ILogger<VadGateNode> logger)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _sessionService = sessionService;
        _configService = configService;
        _vadModelService = vadModelService;
        _sessionId = sessionId;
        _guildId = guildId;
        _logger = logger;
        
        _userVadNodes = new Dictionary<ulong, (SileroVadNode, List<float>)>();
        
        _output = Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(8)
        {
            FullMode = BoundedChannelFullMode.Wait
        });
        
        _ = Task.Run(() => SilenceDetectionLoopAsync(_silenceDetectionCts.Token));
    }
    
    public async Task StartAsync(CancellationToken ct)
    {
        _logger.LogInformation("VadGateNode started for session {SessionId}", _sessionId);
        
        try
        {
            await foreach (var frame in _input.ReadAllAsync(ct))
            {
                // Get or create VAD node and buffer for this user
                if (!_userVadNodes.TryGetValue(frame.UserId, out var userVadData))
                {
                    var session = _vadModelService.CreateSession();
                    var vadNode = new SileroVadNode(session);
                    var newBuffer = new List<float>(VadWindowSize * 2);
                    userVadData = (vadNode, newBuffer);
                    _userVadNodes[frame.UserId] = userVadData;
                    _logger.LogDebug("Created VAD node for user {UserId}", frame.UserId);
                }
                
                var (vad, buffer) = userVadData;
                var state = _userStates.GetOrAdd(frame.UserId, _ => new UserVadState());
                state.LastFrameReceived = DateTime.UtcNow;
                
                buffer.AddRange(frame.Samples.Span);
                
                var config = _configService.Current.Vad;
                
                while (buffer.Count >= VadWindowSize)
                {
                    // Zero-allocation slicing using CollectionsMarshal
                    var chunkSpan = CollectionsMarshal.AsSpan(buffer)[..VadWindowSize];
                    
                    var speechProb = vad.GetSpeechProbability(chunkSpan);
                    
                    buffer.RemoveRange(0, VadWindowSize);
                    
                    var frameIndicatesSpeech = speechProb >= config.StartThreshold;
                    
                    switch (state.IsSpeaking)
                    {
                        case false when frameIndicatesSpeech:
                            state.IsSpeaking = true;
                            state.LastSpeakingFrame = DateTime.UtcNow;
                        
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await _sessionService.UpdateUserSpeakingStatusAsync(_sessionId, _guildId, frame.UserId, true);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex, "Error updating speaking status for user {UserId}", frame.UserId);
                                }
                            }, ct);
                        
                            _logger.LogDebug("User {UserId} started speaking (prob: {Prob:F2})", frame.UserId, speechProb);
                            break;
                        case true:
                        {
                            if (speechProb > config.StopThreshold)
                            {
                                state.LastSpeakingFrame = DateTime.UtcNow;
                            }

                            break;
                        }
                    }
                }
                
                // Only pass through frames when user is speaking
                if (state.IsSpeaking)
                {
                    await _output.Writer.WriteAsync(frame, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("VadGateNode cancelled for session {SessionId}", _sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "VadGateNode error for session {SessionId}", _sessionId);
            throw;
        }
        finally
        {
            _silenceDetectionCts.Cancel();
            _output.Writer.Complete();
            
            // Clean up VAD nodes
            foreach (var (vad, _) in _userVadNodes.Values)
            {
                vad.Dispose();
            }
            _userVadNodes.Clear();
            
            _logger.LogInformation("VadGateNode stopped for session {SessionId}", _sessionId);
        }
    }
    
    private async Task SilenceDetectionLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var now = DateTime.UtcNow;
                var config = _configService.Current.Vad;
                var silenceDuration = TimeSpan.FromMilliseconds(config.SilenceDurationMs);
                
                foreach (var (userId, state) in _userStates)
                {
                    if (state.IsSpeaking && now - state.LastSpeakingFrame > silenceDuration)
                    {
                        // Transition to not speaking
                        state.IsSpeaking = false;
                        
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await _sessionService.UpdateUserSpeakingStatusAsync(_sessionId, _guildId, userId, false);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Error updating speaking status for user {UserId}", userId);
                            }
                        });
                        
                        _logger.LogDebug("User {UserId} stopped speaking (silence)", userId);
                        
                        // Cleanup stale states (no frames for 5 minutes)
                        if (now - state.LastFrameReceived > TimeSpan.FromMinutes(5))
                        {
                            _userStates.TryRemove(userId, out _);
                            
                            if (_userVadNodes.TryGetValue(userId, out var userData))
                            {
                                userData.vad.Dispose();
                                _userVadNodes.Remove(userId);
                            }
                            
                            _logger.LogDebug("Cleaned up stale VAD state for user {UserId}", userId);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
    }
}
