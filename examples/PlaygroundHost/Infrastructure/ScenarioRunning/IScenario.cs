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
    IServiceProvider services,
    IServiceScopeFactory scopeFactory,
    ILogger logger)
{
    public Guid RunId { get; } = runId;

    public string ScenarioRunId => RunId.ToString("D");

    /// <summary>
    /// Root <see cref="IServiceProvider"/> for this scenario run (one async scope for the whole <see cref="IScenario.ExecuteAsync"/>).
    /// Use <see cref="ScopeFactory"/> when you need a separate scope (for example a fresh <c>DbContext</c> per poll).
    /// </summary>
    public IServiceProvider Services { get; } = services;

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
