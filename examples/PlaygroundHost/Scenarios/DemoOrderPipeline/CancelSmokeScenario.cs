using PlaygroundHost.Infrastructure.ScenarioRunning;

namespace PlaygroundHost.Scenarios.DemoOrderPipeline;

/// <summary>Waits until cancelled — used to verify <see cref="ScenarioRunService"/> cancel propagation.</summary>
public sealed class CancelSmokeScenario : IScenario
{
    public string Slug => "cancel-smoke";

    public string Title => "Cancel smoke (tests)";

    public string Description =>
        "Blocks until the run is cancelled or times out. Registered only when Playground:RegisterCancelSmokeScenario=1.";

    public string Topic => "Tests";

    public async Task<ScenarioVerdict> ExecuteAsync(ScenarioExecutionContext context, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return new ScenarioVerdict(false, "Cancelled.");
        }

        return new ScenarioVerdict(true);
    }
}
