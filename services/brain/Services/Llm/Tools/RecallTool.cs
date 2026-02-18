using System.ComponentModel;
using BrainService.Services.Memory;
using Microsoft.Extensions.AI;

namespace BrainService.Services.Llm.Tools;

public class RecallTool(GuildMemoryService memoryService, ToolContextAccessor contextAccessor) : IToolExecutor
{
    public AIFunction AIFunction => AIFunctionFactory.Create(
        async (
            [Description("The key to retrieve")] string key,
            CancellationToken ct) =>
        {
            var ctx = contextAccessor.Current ?? throw new InvalidOperationException("Tool context not set.");
            var value = await memoryService.GetAsync(ctx.GuildId, key.Trim(), ct);
            return value != null ? $"{key} = {value}" : $"No memory found for key '{key}'.";
        },
        name: "recall",
        description: "Retrieve a previously stored memory by key.");
}
