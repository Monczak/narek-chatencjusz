using System.Threading.Channels;
using BrainService.Domain.Audio;

namespace BrainService.Services.Audio.Nodes;

public class UserDemuxerNode : IAudioNode
{
    private readonly ChannelReader<AudioFrame> _input;
    private readonly Action<ulong, ChannelReader<AudioFrame>> _onNewUserStream;
    private readonly ILogger<UserDemuxerNode> _logger;
    private readonly Dictionary<ulong, Channel<AudioFrame>> _userChannels = new();
    
    // Satisfy IAudioNode with a dummy channel
    private readonly Channel<AudioFrame> _output = Channel.CreateBounded<AudioFrame>(1);
    public ChannelReader<AudioFrame> Output => _output.Reader;

    public UserDemuxerNode(
        ChannelReader<AudioFrame> input,
        Action<ulong, ChannelReader<AudioFrame>> onNewUserStream,
        ILogger<UserDemuxerNode> logger)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _onNewUserStream = onNewUserStream ?? throw new ArgumentNullException(nameof(onNewUserStream));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        
        // Immediately complete the dummy output so anything that 
        // accidentally awaits it will just return immediately instead of blocking
        _output.Writer.Complete();
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _logger.LogInformation("UserDemuxerNode started");
        
        try
        {
            await foreach (var frame in _input.ReadAllAsync(ct))
            {
                if (!_userChannels.TryGetValue(frame.UserId, out var channel))
                {
                    // Create a dedicated stream for this user
                    channel = Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(4)
                    {
                        FullMode = BoundedChannelFullMode.DropOldest
                    });
                    
                    _userChannels[frame.UserId] = channel;
                    _logger.LogInformation("Demuxed new audio stream for user {UserId}", frame.UserId);

                    // Notify the graph to plug this new stream into the mixer
                    _onNewUserStream(frame.UserId, channel.Reader);
                }

                await channel.Writer.WriteAsync(frame, ct);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("UserDemuxerNode cancelled");
        }
        finally
        {
            foreach (var channel in _userChannels.Values)
            {
                channel.Writer.Complete();
            }
            _logger.LogInformation("UserDemuxerNode stopped");
        }
    }
}
