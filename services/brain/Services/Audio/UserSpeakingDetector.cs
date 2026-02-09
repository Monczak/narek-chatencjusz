using System.Collections.Concurrent;
using BrainService.Proto;
using BrainService.Services.Configuration;
using BrainService.Services.Session;

namespace BrainService.Services.Audio;

public class UserSpeakingDetector(
    BrainConfigService configService,
    VoiceSessionService sessionService,
    ILogger<UserSpeakingDetector> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<(ulong GuildId, ulong UserId), UserVadState> _states = new();

    private class UserVadState
    {
        public bool IsSpeaking { get; set; }
        public DateTime LastFrameReceived { get; set; }
        public DateTime LastSpeakingFrame { get; set; }
    }

    public bool ProcessFrame(UserAudioFrame frame)
    {
        var config = configService.Current.Vad;
        var key = (frame.GuildId, frame.UserId);
        
        var state =  _states.GetOrAdd(key, k => new UserVadState());
        state.LastFrameReceived = DateTime.UtcNow;
        
        var frameIndicatesSpeech = frame.SpeechProbability >= config.StartThreshold;
        if (!state.IsSpeaking && frameIndicatesSpeech)
        {
            state.IsSpeaking = true;
            state.LastSpeakingFrame = DateTime.UtcNow;
            _ = UpdateSessionStateAsync(frame.GuildId, frame.UserId, true);
        }
        else if (state.IsSpeaking)
        {
            if (frame.SpeechProbability > config.StopThreshold)
            {
                state.LastSpeakingFrame = DateTime.UtcNow;
            }
        }

        return state.IsSpeaking;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var now = DateTime.UtcNow;
            var silenceDuration = TimeSpan.FromMilliseconds(configService.Current.Vad.SilenceDurationMs);

            foreach (var kv in _states)
            {
                var (key, state) = (kv.Key, kv.Value);
                if (state.IsSpeaking && now - state.LastSpeakingFrame > silenceDuration)
                {
                    state.IsSpeaking = false;
                    _ = UpdateSessionStateAsync(key.GuildId, key.UserId, false);

                    if (now - state.LastFrameReceived > TimeSpan.FromMinutes(5))
                    {
                        _states.TryRemove(key, out _);
                    }
                }
            }
        }
    }
    
    private async Task UpdateSessionStateAsync(ulong guildId, ulong userId, bool isSpeaking) 
        => await sessionService.UpdateUserSpeakingStatusAsync(guildId, userId, isSpeaking);
}