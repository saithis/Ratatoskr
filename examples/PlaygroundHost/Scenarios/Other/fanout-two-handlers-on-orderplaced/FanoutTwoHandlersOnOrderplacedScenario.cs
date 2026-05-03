using Microsoft.EntityFrameworkCore;
using PlaygroundHost.Infrastructure;
using PlaygroundHost.Infrastructure.ScenarioRunning;
using PlaygroundHost.Persistence;
using PlaygroundHost.Persistence.Entities;
using Ratatoskr.Core;

namespace PlaygroundHost.Scenarios.Other.FanoutTwoHandlersOnOrderplaced;

public sealed class FanoutTwoHandlersOnOrderplacedScenario : IScenario
{
    public string Slug => "fanout-two-handlers-on-orderplaced";

    public string Title => "Fan-out: two OrderPlaced handlers";

    public string Description =>
        "Both notification handlers run for each successful OrderPlaced delivery; activity log should show at least two successful dispatches.";

    public string Topic => "Other";

    private static async Task<Guid> StageOrderAsync(
        PublisherDbContext db,
        TimeProvider time,
        string runId,
        CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var order = new Order
        {
            Id = Guid.NewGuid(),
            Status = OrderStatus.Placed,
            CreatedAt = now,
            StatusChangedAt = now,
            PublishOrigin = "outbox",
        };
        db.Orders.Add(order);
        var orderIdStr = order.Id.ToString();
        var mpPlaced = new MessageProperties { Id = PlaygroundMessageIds.OrderPlaced(order.Id) };
        var mpCmd = new MessageProperties { Id = PlaygroundMessageIds.ProcessOrderCommand(order.Id) };
        var mpRes = new MessageProperties { Id = PlaygroundMessageIds.ReserveStockInternal(order.Id) };
        PlaygroundCorrelation.AttachToMessageProperties(mpPlaced, runId);
        PlaygroundCorrelation.AttachToMessageProperties(mpCmd, runId);
        PlaygroundCorrelation.AttachToMessageProperties(mpRes, runId);
        db.OutboxMessages.Add(new FanoutTwoHandlersOnOrderplacedOrderPlaced(orderIdStr, runId), mpPlaced);
        db.OutboxMessages.Add(new FanoutTwoHandlersOnOrderplacedProcessOrderCommand(orderIdStr, runId), mpCmd);
        db.OutboxMessages.Add(new FanoutTwoHandlersOnOrderplacedReserveStockInternal(orderIdStr, runId), mpRes);
        await db.SaveChangesAsync(cancellationToken);
        return order.Id;
    }

    public async Task<ScenarioVerdict> ExecuteAsync(ScenarioExecutionContext context, CancellationToken cancellationToken)
    {
        await using var setup = context.ScopeFactory.CreateAsyncScope();
        var sp = setup.ServiceProvider;
        var time = sp.GetRequiredService<TimeProvider>();
        var db = sp.GetRequiredService<PublisherDbContext>();
        var recorder = sp.GetRequiredService<PlaygroundActivityRecorder>();
        var runId = context.ScenarioRunId;
        _ = await StageOrderAsync(db, time, runId, cancellationToken);
        context.StepsCompleted.Add("staged_for_fanout");

        await Task.Delay(TimeSpan.FromSeconds(8), cancellationToken);
        var entries = recorder.GetEntriesForScenarioRun(runId);
        var ok = entries.Count(e =>
            e.Stage == nameof(MessageStage.Dispatched) &&
            e.IsSuccess == true &&
            (e.MessageType ?? "").Contains("order-placed", StringComparison.OrdinalIgnoreCase));
        return ok >= 2
            ? new ScenarioVerdict(true, details: new { matchingRows = ok })
            : new ScenarioVerdict(false, $"Expected at least 2 successful OrderPlaced handler rows for this run; saw {ok}.");
    }
}
