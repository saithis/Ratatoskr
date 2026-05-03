using PlaygroundHost.Infrastructure.ScenarioRunning;

namespace PlaygroundHost.Scenarios.Tests.BlockingHold;

public sealed class BlockingHoldScenario : IScenario
{
    public string Slug => "blocking-hold";

    public string Title => "Blocking hold";

    public string Description =>
        "Sleeps on the cancellation token for about 25 seconds (or until cancelled).";

    public string Topic => "Tests";

    public bool RequiresDangerConfirmation => true;

    public string? DangerConfirmationText =>
        "This scenario blocks for about 25 seconds unless you cancel the run.";

    public async Task<ScenarioVerdict> ExecuteAsync(ScenarioExecutionContext context, CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(25), cancellationToken);
        return new ScenarioVerdict(true);
    }
}
