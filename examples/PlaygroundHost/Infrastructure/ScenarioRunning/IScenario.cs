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
