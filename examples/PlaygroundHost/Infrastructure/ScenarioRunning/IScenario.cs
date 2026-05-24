namespace PlaygroundHost.Infrastructure.ScenarioRunning;

public interface IScenario
{
    /// <summary>Immutable public slug (URL segment).</summary>
    public string Slug { get; }

    public string Title { get; }

    public string Description { get; }

    /// <summary>UI grouping (scenario catalog).</summary>
    public string Topic => "Other";

    /// <summary>When true, dashboard must send an explicit confirmation before starting this scenario.</summary>
    public bool RequiresDangerConfirmation => false;

    /// <summary>Shown in the danger confirmation dialog.</summary>
    public string? DangerConfirmationText => null;

    public Task<ScenarioVerdict> ExecuteAsync(
        ScenarioExecutionContext context,
        CancellationToken cancellationToken
    );
}
