using Microsoft.Extensions.AI;

namespace BrainService.Services.Llm;

public interface IToolExecutor
{
    AIFunction AIFunction { get; }
}
