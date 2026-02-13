using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using BrainService.Domain.Audio;
using BrainService.Proto; // Keeping for reference, but we won't use the parser
using Grpc.Core;
using AudioFrame = BrainService.Domain.Audio.AudioFrame;

namespace BrainService.Services.Audio.Nodes;

public class BotSourceNode(
    PipeReader inputReader, // Changed from IAsyncStreamReader
    string sessionId,
    ILogger<BotSourceNode> logger)
    : IAudioNode
{
    private readonly Channel<AudioFrame> _output = Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(50)
    {
        FullMode = BoundedChannelFullMode.Wait
    });

    public ChannelReader<AudioFrame> Output => _output.Reader;
    
    private const int SamplesPerFrame = 1920; 
    
    // Protobuf Tag for 'pcm_data' (Field 4, WireType 2) -> 0x22
    private const byte PcmDataTag = 0x22; 

    public async Task StartAsync(CancellationToken ct)
    {
        logger.LogInformation("BotSourceNode started (Raw Pipe Mode) for session {SessionId}", sessionId);
        
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await inputReader.ReadAsync(ct);
                var buffer = result.Buffer;

                try
                {
                    // Process as many complete gRPC frames as available in the buffer
                    while (TryReadGrpcFrame(ref buffer, out var payload))
                    {
                        ProcessPayload(payload);
                    }
                }
                finally
                {
                    inputReader.AdvanceTo(buffer.Start, buffer.End);
                }

                if (result.IsCompleted) break;
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("BotSourceNode cancelled");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "BotSourceNode error");
            throw;
        }
        finally
        {
            _output.Writer.TryComplete();
            logger.LogInformation("BotSourceNode stopped");
        }
    }

    private bool TryReadGrpcFrame(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> payload)
    {
        // gRPC Frame Format:
        // [1 byte: Compressed Flag] [4 bytes: Length (Big Endian)] [Payload...]
        
        if (buffer.Length < 5)
        {
            payload = default;
            return false;
        }

        // We assume uncompressed (flag = 0). 
        // If you use compression, this logic needs decompression support.
        
        // Read Length (Bytes 1-4)
        var lengthSlice = buffer.Slice(1, 4);
        Span<byte> lengthBytes = stackalloc byte[4];
        lengthSlice.CopyTo(lengthBytes);
        var messageLength = BinaryPrimitives.ReadUInt32BigEndian(lengthBytes);
        
        var totalFrameSize = 5 + messageLength;
        if (buffer.Length < totalFrameSize)
        {
            payload = default; // Not enough data for full frame yet
            return false;
        }

        payload = buffer.Slice(5, messageLength);
        buffer = buffer.Slice(totalFrameSize); // Advance the reference buffer
        return true;
    }

    private void ProcessPayload(ReadOnlySequence<byte> protoPayload)
    {
        // Manual Protobuf Parsing to find Tag 4 (pcm_data)
        // We iterate the sequence using a SequenceReader
        var reader = new SequenceReader<byte>(protoPayload);

        ulong userId = 0;
        
        while (!reader.End)
        {
            // Read Tag
            if (!TryReadVarint(ref reader, out var tag)) break;
            
            var fieldNumber = tag >> 3;
            var wireType = tag & 7;

            if (fieldNumber == 3) // user_id (uint64)
            {
                if (TryReadVarint(ref reader, out var val)) userId = val;
            }
            else if (fieldNumber == 4) // pcm_data (bytes)
            {
                if (!TryReadVarint(ref reader, out var length)) break;
                
                // CRITICAL: We found the audio data.
                // Instead of allocating a byte[], we process it immediately.
                
                // Sanity check length
                if (length != SamplesPerFrame * 2)
                {
                    reader.Advance((long)length); // Skip invalid
                    continue;
                }

                // Rent float buffer
                var floatSamples = ArrayPool<float>.Shared.Rent(SamplesPerFrame);
                
                // Copy straight from the SequenceReader to our destination
                // We need to slice the sequence at the current position
                var pcmSequence = reader.Sequence.Slice(reader.Position, (long)length);
                
                // Convert (Copy + Transform)
                ConvertPcmToFloat(pcmSequence, floatSamples.AsSpan(0, SamplesPerFrame));
                
                // Advance reader past data
                reader.Advance((long)length);

                // Push to Graph
                var audioFrame = new AudioFrame
                {
                    Samples = floatSamples.AsMemory(0, SamplesPerFrame),
                    UserId = userId,
                    Timestamp = DateTime.UtcNow
                };

                // Manual Drop Logic (from previous fix)
                if (!_output.Writer.TryWrite(audioFrame))
                {
                    if (_output.Reader.TryRead(out var droppedFrame))
                    {
                        if (MemoryMarshal.TryGetArray(droppedFrame.Samples, out var segment) && segment.Array != null)
                            ArrayPool<float>.Shared.Return(segment.Array);
                    }
                    if (!_output.Writer.TryWrite(audioFrame))
                        ArrayPool<float>.Shared.Return(floatSamples);
                }
            }
            else
            {
                // Skip unknown fields
                if (wireType == 0) { TryReadVarint(ref reader, out _); } // Varint
                else if (wireType == 2) // Length Delimited
                {
                    if (TryReadVarint(ref reader, out var len)) reader.Advance((long)len);
                }
                else { break; } // Not handling other wire types for simplicity
            }
        }
    }

    // Helper for Varint (Protobuf base-128)
    private static bool TryReadVarint(ref SequenceReader<byte> reader, out ulong value)
    {
        value = 0;
        int shift = 0;
        while (reader.TryRead(out byte b))
        {
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
            shift += 7;
            if (shift > 64) return false; // Overflow
        }
        return false;
    }

    private static void ConvertPcmToFloat(ReadOnlySequence<byte> pcmSequence, Span<float> floatSamples)
    {
        // Optimization: If sequence is single segment (common), get span directly
        if (pcmSequence.IsSingleSegment)
        {
            var span = pcmSequence.FirstSpan;
            for (var i = 0; i < floatSamples.Length; i++)
            {
                var pcmSample = BinaryPrimitives.ReadInt16LittleEndian(span.Slice(i * 2, 2));
                floatSamples[i] = pcmSample / 32768f;
            }
        }
        else
        {
            // Slow path for fragmented sequence (copy to stack buffer first)
            Span<byte> temp = stackalloc byte[pcmSequence.Length > 4096 ? 0 : (int)pcmSequence.Length];
            if (temp.Length > 0)
            {
                pcmSequence.CopyTo(temp);
                for (var i = 0; i < floatSamples.Length; i++)
                {
                    var pcmSample = BinaryPrimitives.ReadInt16LittleEndian(temp.Slice(i * 2, 2));
                    floatSamples[i] = pcmSample / 32768f;
                }
            }
        }
    }
}
