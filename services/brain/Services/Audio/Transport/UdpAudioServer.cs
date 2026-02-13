using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using BrainService.Services.Audio;

namespace BrainService.Services.Audio.Transport;

/// <summary>
/// Single UDP socket shared by all sessions.
/// Inbound packets are routed to the appropriate SessionAudioGraph.
/// Outbound audio is sent back to the bot's source endpoint.
/// </summary>
public sealed class UdpAudioServer(ILogger<UdpAudioServer> logger) : IHostedService, IDisposable
{
    // session GUID → graph (weak ref so a disposed graph doesn't block GC)
    private readonly ConcurrentDictionary<Guid, SessionAudioGraph> _sessions  = new();
    // session GUID → bot's UDP endpoint (learned from first inbound packet)
    private readonly ConcurrentDictionary<Guid, IPEndPoint>        _endpoints = new();

    private UdpClient?            _udp;
    private CancellationTokenSource? _cts;
    private Task?                 _receiveTask;

    // ── IHostedService ─────────────────────────────────────────────────────────

    public Task StartAsync(CancellationToken ct)
    {
        _udp = new UdpClient(AudioWireProtocol.UdpPort);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token), ct);
        logger.LogInformation("UdpAudioServer listening on port {Port}", AudioWireProtocol.UdpPort);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _cts?.Cancel();
        if (_receiveTask != null)
            await _receiveTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    public void Dispose()
    {
        _cts?.Dispose();
        _udp?.Dispose();
    }

    // ── Session registration ───────────────────────────────────────────────────

    public void RegisterSession(Guid sessionId, SessionAudioGraph graph)
    {
        _sessions[sessionId] = graph;
        logger.LogDebug("Registered UDP session {SessionId}", sessionId);
    }

    public void UnregisterSession(Guid sessionId)
    {
        _sessions.TryRemove(sessionId, out _);
        _endpoints.TryRemove(sessionId, out _);
        logger.LogDebug("Unregistered UDP session {SessionId}", sessionId);
    }

    // ── Outbound (called from mixer output loop on dedicated thread) ───────────

    public void SendAudio(Guid sessionId, ulong guildId, ReadOnlySpan<byte> pcm)
    {
        if (!_endpoints.TryGetValue(sessionId, out var ep) || _udp is null) return;

        Span<byte> packet = stackalloc byte[AudioWireProtocol.OutboundPacketSize];
        packet[0] = AudioWireProtocol.TypeAudioOut;
        BinaryPrimitives.WriteUInt64LittleEndian(packet[1..], guildId);
        pcm.CopyTo(packet[AudioWireProtocol.OutboundHeaderSize..]);

        try
        {
            _udp.Send(packet, ep);
        }
        catch (Exception ex) { logger.LogDebug("UDP send failed: {Message}", ex.Message); }
    }

    // ── Receive loop ───────────────────────────────────────────────────────────

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await _udp!.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogWarning("UDP receive error: {Message}", ex.Message);
                await Task.Delay(100, ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                continue;
            }

            var buf = result.Buffer;
            if (buf.Length < AudioWireProtocol.InboundPacketSize) continue;
            if (buf[0] != AudioWireProtocol.TypeAudioIn) continue;

            var guildId = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(1, 8));
            var userId  = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(9, 8));

            Guid sessionId;
            try { sessionId = new Guid(buf.AsSpan(17, 16), bigEndian: true); }
            catch { continue; }

            // Always update endpoint so reconnects work
            _endpoints[sessionId] = (IPEndPoint)result.RemoteEndPoint;

            if (_sessions.TryGetValue(sessionId, out var graph))
                graph.PushUserAudio(userId, buf.AsSpan(AudioWireProtocol.InboundHeaderSize, AudioWireProtocol.PcmFrameSize));
        }
    }
}