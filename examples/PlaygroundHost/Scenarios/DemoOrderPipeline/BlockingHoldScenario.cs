using PlaygroundHost.Infrastructure.ScenarioRunning;

namespace PlaygroundHost.Scenarios.DemoOrderPipeline;

/// <summary>Holds the scenario single-flight gate long enough for concurrent-run tests.</summary>
public sealed class BlockingHoldScenario : IScenario
{
    public string Slug => "blocking-hold";

    public string Title => "Blocking hold (tests)";

    public string Description =>
        "Sleeps on the cancellation token for ~25s (or until cancelled). Registered only when Playground:RegisterBlockingScenario=1.";

    public string Topic => "Tests";

    public async Task<ScenarioVerdict> ExecuteAsync(ScenarioExecutionContext context, CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(25), cancellationToken);
        return new ScenarioVerdict(true);
    }
}
