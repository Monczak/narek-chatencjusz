namespace BrainService.Services.Audio.Transport;

public static class AudioWireProtocol
{
    public const int UdpPort = 5051;
    public const int PcmFrameSize = 3840; // 48 kHz stereo 16-bit 20 ms

    // Inbound
    public const byte TypeAudioIn = 0x01;
    public const int InboundHeaderSize = 33; // 1+8+8+16
    public const int InboundPacketSize = InboundHeaderSize + PcmFrameSize; // 3873

    // Outbound
    public const byte TypeAudioOut = 0x02;
    public const int OutboundHeaderSize = 9; // 1+8
    public const int OutboundPacketSize = OutboundHeaderSize + PcmFrameSize; // 3849
}
