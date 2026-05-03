using PlaygroundHost.Infrastructure;
using PlaygroundHost.Infrastructure.ScenarioRunning;
using PlaygroundHost.Persistence;
using Ratatoskr.Core;

namespace PlaygroundHost.Scenarios.DemoOrderPipeline;

public sealed class EfCoreInternalCommandScenario : IScenario
{
    public string Slug => "efcore-internal-command";

    public string Title => "EF Core internal command";

    public string Description =>
        "ReserveStockInternal is staged in the same SaveChanges as other outbox messages; activity should show EF Core transport handling.";

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
        context.StepsCompleted.Add("staged_with_internal");

        await Task.Delay(TimeSpan.FromSeconds(6), cancellationToken);
        var entries = recorder.GetEntriesForScenarioRun(runId);
        var hit = entries.Any(e =>
            (e.MessageType ?? "").Contains("ReserveStock", StringComparison.OrdinalIgnoreCase) &&
            ((e.TransportName ?? "").Contains("EfCore", StringComparison.OrdinalIgnoreCase) ||
             e.Stage == nameof(MessageStage.InboxDispatched) ||
             e.Stage == nameof(MessageStage.Dispatched)));
        return hit
            ? new ScenarioVerdict(true)
            : new ScenarioVerdict(false, "No ReserveStockInternal / EF Core transport activity captured for this run yet.");
    }
}
