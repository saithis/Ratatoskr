using PlaygroundHost.Infrastructure;
using PlaygroundHost.Infrastructure.ScenarioRunning;
using PlaygroundHost.Persistence;
using Ratatoskr.Core;

namespace PlaygroundHost.Scenarios.DemoOrderPipeline;

public sealed class FanoutTwoHandlersScenario : IScenario
{
    public string Slug => "fanout-two-handlers-on-orderplaced";

    public string Title => "Fan-out: two OrderPlaced handlers";

    public string Description =>
        "Both notification handlers run for each successful OrderPlaced delivery; activity log should show at least two successful dispatches.";

    public string Topic => "Other";

    public async Task<ScenarioVerdict> ExecuteAsync(ScenarioExecutionContext context, CancellationToken cancellationToken)
    {
        await using var setup = context.ScopeFactory.CreateAsyncScope();
        var sp = setup.ServiceProvider;
        var time = sp.GetRequiredService<TimeProvider>();
        var db = sp.GetRequiredService<PublisherDbContext>();
        var recorder = sp.GetRequiredService<PlaygroundActivityRecorder>();
        var runId = context.ScenarioRunId;
        ScenarioToggleReset.ApplyBaseline(sp);
        _ = await OrderOutboxStaging.StageOutboxOrderAsync(db, time, runId, cancellationToken);
        context.StepsCompleted.Add("staged_for_fanout");

        await Task.Delay(TimeSpan.FromSeconds(8), cancellationToken);
        var entries = recorder.GetEntriesForScenarioRun(runId);
        var ok = entries.Count(e =>
            e.Stage == nameof(MessageStage.Dispatched) &&
            e.IsSuccess == true &&
            (e.MessageType ?? "").Contains("OrderPlaced", StringComparison.OrdinalIgnoreCase));
        return ok >= 2
            ? new ScenarioVerdict(true, details: new { matchingRows = ok })
            : new ScenarioVerdict(false, $"Expected at least 2 successful OrderPlaced handler rows for this run; saw {ok}.");
    }
}
