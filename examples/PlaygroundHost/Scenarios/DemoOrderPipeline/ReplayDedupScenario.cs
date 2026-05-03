using PlaygroundHost.Infrastructure;
using PlaygroundHost.Infrastructure.ScenarioRunning;
using PlaygroundHost.Persistence;
using PlaygroundHost.Persistence.Entities;
using PlaygroundHost.Scenarios.DemoOrderPipeline.Messages;
using Ratatoskr;
using Ratatoskr.Core;

namespace PlaygroundHost.Scenarios.DemoOrderPipeline;

public sealed class ReplayDedupScenario(IRatatoskr bus) : IScenario
{
    public string Slug => "replay-dedups";

    public string Title => "Replay (dedup vs double delivery)";

    public string Description =>
        "After Fulfilled, replay publishes the same CloudEvents ids; notifications may see duplicate OrderPlaced activity while inventory dedups the command inbox.";

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
        var orderId = await OrderOutboxStaging.StageOutboxOrderAsync(db, time, runId, cancellationToken);
        var v = await ScenarioAssertions.OrderEventuallyAsync(
            context.ScopeFactory,
            orderId,
            OrderStatus.Fulfilled,
            TimeSpan.FromSeconds(90),
            time,
            cancellationToken);
        if (!v.Passed)
            return v;

        var before = recorder.GetEntriesForOrder(orderId).Count;
        var orderIdStr = orderId.ToString();
        var p1 = new MessageProperties { Id = PlaygroundMessageIds.OrderPlaced(orderId) };
        var p2 = new MessageProperties { Id = PlaygroundMessageIds.ProcessOrderCommand(orderId) };
        var p3 = new MessageProperties { Id = PlaygroundMessageIds.ReserveStockInternal(orderId) };
        PlaygroundCorrelation.AttachToMessageProperties(p1, runId);
        PlaygroundCorrelation.AttachToMessageProperties(p2, runId);
        PlaygroundCorrelation.AttachToMessageProperties(p3, runId);
        await bus.PublishDirectAsync(
            new OrderPlaced { OrderId = orderIdStr, ScenarioRunId = runId },
            p1,
            cancellationToken);
        await bus.PublishDirectAsync(
            new ProcessOrderCommand { OrderId = orderIdStr, ScenarioRunId = runId },
            p2,
            cancellationToken);
        await bus.PublishDirectAsync(
            new ReserveStockInternal { OrderId = orderIdStr, ScenarioRunId = runId },
            p3,
            cancellationToken);
        context.StepsCompleted.Add("replay_direct_publish");

        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        var after = recorder.GetEntriesForOrder(orderId).Count;
        var nPlaced = recorder.GetEntriesForOrder(orderId).Count(e =>
            e.Stage == nameof(MessageStage.Dispatched) &&
            e.IsSuccess == true &&
            (e.MessageType ?? "").Contains("OrderPlaced", StringComparison.OrdinalIgnoreCase));
        var pass = after > before && nPlaced >= 2;
        return pass
            ? new ScenarioVerdict(true, details: new { rowsBefore = before, rowsAfter = after, notifDispatched = nPlaced })
            : new ScenarioVerdict(false, $"Activity after replay did not match expectations (before={before}, after={after}, notifOrderPlacedDispatched={nPlaced}).");
    }
}
