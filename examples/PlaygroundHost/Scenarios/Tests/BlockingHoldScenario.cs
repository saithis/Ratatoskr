using PlaygroundHost.Infrastructure.ScenarioRunning;
using Ratatoskr;

namespace PlaygroundHost.Scenarios.Tests;

public sealed class BlockingHoldScenario : IPlaygroundScenario
{
    public static IReadOnlyList<PlaygroundRabbitDepthQueue> RabbitDepthQueues => [];

    public static void RegisterRatatoskrTopology(RatatoskrBuilder bus) { }

    public string Slug => "blocking-hold";

    public string Title => "Blocking hold";

    public string Description =>
        "Sleeps on the cancellation token for about 25 seconds (or until cancelled).";

    public string Topic => "Tests";

    public bool RequiresDangerConfirmation => true;

    public string DangerConfirmationText =>
        "This scenario blocks for about 25 seconds unless you cancel the run.";

    public async Task<ScenarioVerdict> ExecuteAsync(
        ScenarioExecutionContext context,
        CancellationToken cancellationToken
    )
    {
        await Task.Delay(TimeSpan.FromSeconds(25), cancellationToken);
        return new ScenarioVerdict(true);
    }
}
