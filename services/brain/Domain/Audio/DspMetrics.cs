namespace BrainService.Domain.Audio;

public record DspNodeMetrics(string Name, int QueueDepth);

public record DspSessionMetrics(
    string SessionId, 
    IReadOnlyList<DspNodeMetrics> Nodes, 
    double AverageLatencyMs,
    DateTime Timestamp);
