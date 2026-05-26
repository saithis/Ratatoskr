using PlaygroundHost.Infrastructure.ScenarioRunning;
using Ratatoskr;

namespace PlaygroundHost.Scenarios.Tests;

public sealed class CancelSmokeScenario : IPlaygroundScenario
{
    public static IReadOnlyList<PlaygroundRabbitQueue> RabbitQueues => [];

    public static void RegisterRatatoskrTopology(RatatoskrBuilder bus) { }

    public string Slug => "cancel-smoke";

    public string Title => "Cancel smoke";

    public string Description =>
        "Waits until cancelled or times out; use POST /api/playground/runs/{runId}/cancel to verify cooperative cancellation.";

    public string Topic => "Tests";

    public async Task<ScenarioVerdict> ExecuteAsync(
        ScenarioExecutionContext context,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return new ScenarioVerdict(passed: true, "Cancelled as expected.");
        }

        return new ScenarioVerdict(passed: false, "Expected cancellation or timeout.");
    }
}
