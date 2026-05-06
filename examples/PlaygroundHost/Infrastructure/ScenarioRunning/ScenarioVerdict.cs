namespace PlaygroundHost.Infrastructure.ScenarioRunning;

public sealed class ScenarioVerdict(bool passed, string? reason = null, object? details = null)
{
    public bool Passed { get; } = passed;
    public string? Reason { get; } = reason;
    public object? Details { get; } = details;
}
