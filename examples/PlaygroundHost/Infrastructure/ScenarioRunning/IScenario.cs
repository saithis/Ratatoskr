namespace PlaygroundHost.Infrastructure.ScenarioRunning;

public interface IScenario
{
    /// <summary>Immutable public slug (URL segment).</summary>
    string Slug { get; }

    string Title { get; }

    string Description { get; }

    /// <summary>UI grouping (scenario catalog).</summary>
    string Topic => "Other";

    /// <summary>When true, dashboard must send an explicit confirmation before starting this scenario.</summary>
    bool RequiresDangerConfirmation => false;

    /// <summary>Shown in the danger confirmation dialog.</summary>
    string? DangerConfirmationText => null;

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
