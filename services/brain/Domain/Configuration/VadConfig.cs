namespace BrainService.Domain.Configuration;

public class VadConfig
{
    public float StartThreshold { get; set; } = 0.6f;
    public float StopThreshold { get; set; } = 0.4f;
    public int SilenceDurationMs { get; set; } = 300;
    public int PreBufferFrameCount { get; set; } = 10;
}