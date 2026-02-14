using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using BrainService.Domain.Audio;
using BrainService.Services.Audio.Nodes;
using BrainService.Services.Audio.Vad;
using BrainService.Services.Configuration;

namespace BrainService.Services.Audio.Graph;

public sealed class UserPipeline
{
    private readonly Channel<AudioFrame> _source;
    private readonly CancellationToken _ct;

    public IReadOnlyList<IAudioNode> Nodes { get; }
    
    private const int SamplesPerFrame = 1920; // 48 kHz stereo float

    public UserPipeline(
        ulong userId,
        MixerNode mixer,
        BrainConfigService config,
        SileroVadModelService vadModel,
        Action<bool>? speakingStateChanged,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        _ct = ct;

        _source = Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(8)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = true,
            SingleReader = true,
        });

        var stereoToMono = new ChannelConverterNode(
            _source.Reader,
            monoToStereo: false,
            loggerFactory.CreateLogger<ChannelConverterNode>()
        );
        
        var resampleDown = new ResamplerNode(
            stereoToMono.Output,
            48000,
            16000,
            loggerFactory.CreateLogger<ResamplerNode>()
        );
        
        var vad = new VadGateNode(
            resampleDown.Output,
            new SileroVadWrapper(vadModel.CreateSession()),
            config,
            loggerFactory.CreateLogger<VadGateNode>(),
            speakingStateChanged
        );
        
        var resampleUp = new ResamplerNode(
            vad.Output,
            16000,
            48000,
            loggerFactory.CreateLogger<ResamplerNode>()
        );
        
        var monoToStereo = new ChannelConverterNode(
            resampleUp.Output,
            monoToStereo: true,
            loggerFactory.CreateLogger<ChannelConverterNode>()
        );
        
        mixer.AddInput($"user_{userId}", monoToStereo.Output);

        Nodes = [stereoToMono, resampleDown, vad, resampleUp, monoToStereo];
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
            Samples = floats.AsMemory(0, SamplesPerFrame),
            UserId = userId,
            Timestamp = DateTime.UtcNow,
        };

        if (!_source.Writer.TryWrite(frame))
        {
            if (MemoryMarshal.TryGetArray(frame.Samples, out var seg) && seg.Array != null)
            {
                ArrayPool<float>.Shared.Return(seg.Array);
            }
        }
    }

    public void Start(ConcurrentBag<Task> taskBag)
    {
        foreach (var node in Nodes)
        {
            taskBag.Add(Task.Factory.StartNew(
                    () => node.StartAsync(_ct),
                    _ct,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default).Unwrap()
            );
            
        }
    }
}