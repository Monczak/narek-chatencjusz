using System.Buffers;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using BrainService.Domain.Audio;

namespace BrainService.Services.Audio.Nodes;

public abstract class AudioFilterNode(ChannelReader<AudioFrame> input, ILogger logger, int outputCapacity = 4)
    : IAudioNode
{
    protected ILogger Logger => logger;
    
    private readonly ChannelReader<AudioFrame> _input = input ?? throw new ArgumentNullException(nameof(input));
    private readonly Channel<AudioFrame> _output = Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(outputCapacity)
    {
        FullMode = BoundedChannelFullMode.Wait
    });

    public ChannelReader<AudioFrame> Output => _output.Reader;
    public int QueueDepth => _output.Reader.Count;

    public async Task StartAsync(CancellationToken ct)
    {
        Logger.LogInformation("{Node} started", GetType().Name);
        try
        {
            await foreach (var inputFrame in _input.ReadAllAsync(ct))
            {
                var ownershipTransferred = false;

                try
                {
                    var outputFrame = await ProcessFrameAsync(inputFrame, ct);
                    if (outputFrame.HasValue)
                    {
                        await _output.Writer.WriteAsync(outputFrame.Value, ct);

                        if (IsSameBuffer(inputFrame, outputFrame.Value))
                        {
                            ownershipTransferred = true;
                        }
                    }
                }
                finally
                {
                    if (!ownershipTransferred)
                    {
                        ReturnBuffer(inputFrame);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "{Node} error", GetType().Name);
            throw;
        }
        finally
        {
            _output.Writer.Complete();
            Logger.LogInformation("{Node} stopped", GetType().Name);
        }
    }
    protected abstract ValueTask<AudioFrame?> ProcessFrameAsync(AudioFrame inputFrame, CancellationToken ct);
    
    protected static float[] RentBuffer(int size) 
        => ArrayPool<float>.Shared.Rent(size);

    protected static void ReturnRawBuffer(float[] buffer) 
        => ArrayPool<float>.Shared.Return(buffer);

    private void ReturnBuffer(AudioFrame frame)
    {
        if (MemoryMarshal.TryGetArray(frame.Samples, out var seg) && seg.Array != null)
        {
            ArrayPool<float>.Shared.Return(seg.Array);
        }
    }

    private bool IsSameBuffer(AudioFrame a, AudioFrame b)
    {
        if (MemoryMarshal.TryGetArray(a.Samples, out var segA) &&
            MemoryMarshal.TryGetArray(b.Samples, out var segB))
        {
            return segA.Array == segB.Array;
        }

        return false;
    }

}