using Microsoft.EntityFrameworkCore;
using PlaygroundHost.Infrastructure;
using PlaygroundHost.Infrastructure.ScenarioRunning;
using PlaygroundHost.Persistence;
using PlaygroundHost.Persistence.Entities;
using Ratatoskr.Core;

namespace PlaygroundHost.Scenarios.Other.EfcoreInternalCommand;

public sealed class EfcoreInternalCommandScenario : IScenario
{
    public string Slug => "efcore-internal-command";

    public string Title => "EF Core internal command";

    public string Description =>
        "ReserveStockInternal is staged in the same SaveChanges as other outbox messages; activity should show EF Core transport handling.";

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
        db.OutboxMessages.Add(new EfcoreInternalCommandOrderPlaced(orderIdStr, runId), mpPlaced);
        db.OutboxMessages.Add(new EfcoreInternalCommandProcessOrderCommand(orderIdStr, runId), mpCmd);
        db.OutboxMessages.Add(new EfcoreInternalCommandReserveStockInternal(orderIdStr, runId), mpRes);
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
        context.StepsCompleted.Add("staged_with_internal");

        await Task.Delay(TimeSpan.FromSeconds(6), cancellationToken);
        var entries = recorder.GetEntriesForScenarioRun(runId);
        var hit = entries.Any(e =>
            (e.MessageType ?? "").Contains("reserve-stock-internal", StringComparison.OrdinalIgnoreCase) &&
            ((e.TransportName ?? "").Contains("EfCore", StringComparison.OrdinalIgnoreCase) ||
             e.Stage == nameof(MessageStage.InboxDispatched) ||
             e.Stage == nameof(MessageStage.Dispatched)));
        return hit
            ? new ScenarioVerdict(true)
            : new ScenarioVerdict(false, "No ReserveStockInternal / EF Core transport activity captured for this run yet.");
    }
}
