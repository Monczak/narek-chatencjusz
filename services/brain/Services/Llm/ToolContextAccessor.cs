namespace BrainService.Services.Llm;

public class ToolContextAccessor
{
    private readonly AsyncLocal<ToolExecutionContext?> _current = new();

    public ToolExecutionContext? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }
}