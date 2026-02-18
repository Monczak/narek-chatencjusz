using Microsoft.Extensions.AI;

namespace BrainService.Services.Llm;

public class ToolRegistry(IEnumerable<IToolExecutor> executors)
{
    public IReadOnlyList<AIFunction> AIFunctions => executors.Select(e => e.AIFunction).ToList();
}
