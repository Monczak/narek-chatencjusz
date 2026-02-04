using BrainService.Proto;
using Grpc.Core;

namespace BrainService.Services;

public class BrainGrpcService(ILogger<BrainGrpcService> logger) : Brain.BrainBase
{
    public override Task<PingResponse> Ping(PingRequest request, ServerCallContext context)
    {
        logger.LogInformation("Ping request received");
        return Task.FromResult(new PingResponse
        {
            Message = $"Pong: {request.Message}",
        });
    }
}