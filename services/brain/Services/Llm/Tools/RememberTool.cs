using System.ComponentModel;
using BrainService.Services.Memory;
using Microsoft.Extensions.AI;

namespace BrainService.Services.Llm.Tools;

public class RememberTool(GuildMemoryService memoryService, ToolContextAccessor contextAccessor) : IToolExecutor
{
    public AIFunction AIFunction { get; } = AIFunctionFactory.Create(
        async (
            [Description("The key to store the value under")] string key,
            [Description("The value to store")] string value,
            CancellationToken ct) =>
        {
            var ctx = contextAccessor.Current ?? throw new InvalidOperationException("Tool context not set.");
            await memoryService.SetAsync(ctx.GuildId, key.Trim(), value.Trim(), ct);
            return $"Stored: {key} = {value}";
        },
        name: "remember",
        description: "Store a piece of information in persistent guild memory. Persists across sessions and is injected into future context.");
}
