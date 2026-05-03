namespace PlaygroundHost.Infrastructure.ScenarioRunning;

public interface IScenario
{
    /// <summary>Immutable public slug (URL segment).</summary>
    string Slug { get; }

    string Title { get; }

    string Description { get; }

    /// <summary>UI grouping (scenario catalog).</summary>
    string Topic => "Other";

    Task<ScenarioVerdict> ExecuteAsync(ScenarioExecutionContext context, CancellationToken cancellationToken);
}

public sealed class ScenarioExecutionContext(
    Guid runId,
    IServiceScopeFactory scopeFactory,
    ILogger logger)
{
    public Guid RunId { get; } = runId;

    public string ScenarioRunId => RunId.ToString("D");

    public IServiceScopeFactory ScopeFactory { get; } = scopeFactory;

    public ILogger Logger { get; } = logger;

    public List<string> StepsCompleted { get; } = [];
}

public sealed class ScenarioVerdict(bool passed, string? reason = null, object? details = null)
{
    public bool Passed { get; } = passed;
    public string? Reason { get; } = reason;
    public object? Details { get; } = details;
}
