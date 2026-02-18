using BrainService.Proto.Brain;
using Microsoft.Extensions.AI;

namespace BrainService.Services.Llm.Tools;

public class EndSessionTool(
    NodeRegistryService nodeRegistry,
    CommandPublisher commandPublisher,
    ToolContextAccessor contextAccessor,
    ILogger<EndSessionTool> logger) : IToolExecutor
{
    public AIFunction AIFunction { get; } = AIFunctionFactory.Create(
        async (CancellationToken ct) =>
        {
            var ctx = contextAccessor.Current ?? throw new InvalidOperationException("Tool context not set.");

            var nodeId = await nodeRegistry.GetNodeForGuildAsync(ctx.GuildId);
            if (string.IsNullOrEmpty(nodeId))
            {
                logger.LogWarning("EndSessionTool: no node found for guild {GuildId}", ctx.GuildId);
                return "No active bot node found for this guild.";
            }

            await commandPublisher.PublishCommandAsync(nodeId, new BrainCommand
            {
                Disconnect = new DisconnectVoice
                {
                    Guild = new GuildContext { Id = ctx.GuildId },
                    SessionId = ctx.SessionId,
                }
            });

            logger.LogInformation("EndSessionTool: disconnect command sent for session {SessionId}", ctx.SessionId);
            return "Session ended.";
        },
        name: "end_session",
        description: "End the current voice session and disconnect from the voice channel.");
}
