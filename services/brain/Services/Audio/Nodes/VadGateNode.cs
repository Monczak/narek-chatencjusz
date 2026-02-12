using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using BrainService.Domain.Audio;
using BrainService.Services.Configuration;
using BrainService.Services.Session;

namespace BrainService.Services.Audio.Nodes;

public class VadGateNode : IAudioNode
{
    private readonly Channel<AudioFrame> _output;
    private readonly ChannelReader<AudioFrame> _input;
    private readonly VoiceSessionService _sessionService;
    private readonly BrainConfigService _configService;
    private readonly SileroVadModelService _vadModelService;
    private readonly string _sessionId;
    private readonly ulong _guildId;
    private readonly ILogger<VadGateNode> _logger;
    
    private readonly CancellationTokenSource _silenceDetectionCts = new();
    
    // Per-user state
    private readonly Dictionary<ulong, UserVadData> _userVad = new();
    private readonly ConcurrentDictionary<ulong, UserVadState> _userStates = new();
    
    private sealed class UserVadData(SileroVadWrapper vad, int preBufferCapacity)
    {
        public SileroVadWrapper Vad { get; } = vad;
        public List<float> SampleBuffer { get; } = new(VadWindowSize * 2);
        public Queue<AudioFrame> PreBuffer { get; } = new(preBufferCapacity + 1);
        public int PreBufferCapacity { get; } = preBufferCapacity;
    }
    
    private sealed class UserVadState
    {
        public bool IsSpeaking;
        public DateTime LastSpeakingFrame;
        public DateTime LastFrameReceived;
    }
    
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
                if (!_userVad.TryGetValue(frame.UserId, out var userData))
                {
                    var preBufferCapacity = _configService.Current.Vad.PreBufferFrameCount;
                    var onnxSession = _vadModelService.CreateSession();
                    var vadNode = new SileroVadWrapper(onnxSession);
                    userData = new UserVadData(vadNode, preBufferCapacity);
                    _userVad[frame.UserId] = userData;
                    _logger.LogDebug(
                        "Created VAD for user {UserId}, pre-buffer={Frames} frames (~{Ms}ms)",
                        frame.UserId, preBufferCapacity, preBufferCapacity * 32);
                }

                var state = _userStates.GetOrAdd(frame.UserId, _ => new UserVadState());
                state.LastFrameReceived = DateTime.UtcNow;

                userData.SampleBuffer.AddRange(frame.Samples.Span);

                var config = _configService.Current.Vad;

                while (userData.SampleBuffer.Count >= VadWindowSize)
                {
                    var chunkSpan = CollectionsMarshal.AsSpan(userData.SampleBuffer)[..VadWindowSize];
                    var prob = userData.Vad.GetSpeechProbability(chunkSpan);
                    userData.SampleBuffer.RemoveRange(0, VadWindowSize);

                    if (!state.IsSpeaking && prob >= config.StartThreshold)
                    {
                        state.IsSpeaking = true;
                        state.LastSpeakingFrame = DateTime.UtcNow;

                        _logger.LogDebug(
                            "User {UserId} speech start (p={Prob:F2}), flushing {N} pre-buffer frames",
                            frame.UserId, prob, userData.PreBuffer.Count);

                        // Flush pre-roll – downstream takes ownership, don't return these
                        while (userData.PreBuffer.TryDequeue(out var preFrame))
                            await _output.Writer.WriteAsync(preFrame, ct);

                        _ = Task.Run(() => NotifySpeaking(frame.UserId, true), ct);
                    }
                    else if (state.IsSpeaking && prob > config.StopThreshold)
                    {
                        state.LastSpeakingFrame = DateTime.UtcNow;
                    }
                }

                if (state.IsSpeaking)
                {
                    await _output.Writer.WriteAsync(frame, ct);
                }
                else
                {
                    // Add to circular pre-buffer; evict and return oldest if full
                    if (userData.PreBuffer.Count >= userData.PreBufferCapacity)
                    {
                        if (userData.PreBuffer.TryDequeue(out var evicted))
                            ReturnFrame(evicted);
                    }

                    userData.PreBuffer.Enqueue(frame);
                }
            }
        }
        catch (OperationCanceledException)
        {
             // Normal shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "VadGateNode error for session {SessionId}", _sessionId);
            throw;
        }
        finally
        {
            await _silenceDetectionCts.CancelAsync();
            _output.Writer.Complete();
            
            foreach (var data in _userVad.Values)
            {
                data.Vad.Dispose();
                while (data.PreBuffer.TryDequeue(out var f))
                    ReturnFrame(f);
            }
            _userVad.Clear();
            
            _logger.LogInformation("VadGateNode stopped for session {SessionId}", _sessionId);
        }
    }
    
    private static void ReturnFrame(AudioFrame frame)
    {
        if (MemoryMarshal.TryGetArray(frame.Samples, out var seg) && seg.Array != null)
            ArrayPool<float>.Shared.Return(seg.Array);
    }
    
    private async Task NotifySpeaking(ulong userId, bool isSpeaking)
    {
        try
        {
            await _sessionService.UpdateUserSpeakingStatusAsync(_sessionId, _guildId, userId, isSpeaking);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating speaking status for user {UserId}", userId);
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
                var silenceDuration = TimeSpan.FromMilliseconds(_configService.Current.Vad.SilenceDurationMs);

                foreach (var (userId, state) in _userStates)
                {
                    if (state.IsSpeaking && now - state.LastSpeakingFrame > silenceDuration)
                    {
                        state.IsSpeaking = false;
                        _logger.LogDebug("User {UserId} stopped speaking (silence)", userId);
                        _ = Task.Run(() => NotifySpeaking(userId, false), ct);
                    }

                    // Evict stale entries after 5 minutes of inactivity
                    if (now - state.LastFrameReceived > TimeSpan.FromMinutes(5))
                    {
                        _userStates.TryRemove(userId, out _);
                        if (_userVad.TryGetValue(userId, out var data))
                        {
                            data.Vad.Dispose();
                            while (data.PreBuffer.TryDequeue(out var f))
                                ReturnFrame(f);
                            _userVad.Remove(userId);
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
