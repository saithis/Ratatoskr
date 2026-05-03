using Microsoft.EntityFrameworkCore;
using PlaygroundHost.Infrastructure;
using PlaygroundHost.Persistence;
using PlaygroundHost.Persistence.Entities;
using PlaygroundHost.Scenarios.DemoOrderPipeline.Messages;
using Ratatoskr.Core;

namespace PlaygroundHost.Scenarios.DemoOrderPipeline;

public static class OrderOutboxStaging
{
    /// <summary>Creates an order and stages outbox + internal messages with correlation for the scenario run.</summary>
    public static async Task<Guid> StageOutboxOrderAsync(
        PublisherDbContext db,
        TimeProvider time,
        string scenarioRunId,
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
        PlaygroundCorrelation.AttachToMessageProperties(mpPlaced, scenarioRunId);
        PlaygroundCorrelation.AttachToMessageProperties(mpCmd, scenarioRunId);
        PlaygroundCorrelation.AttachToMessageProperties(mpRes, scenarioRunId);
        db.OutboxMessages.Add(
            new OrderPlaced { OrderId = orderIdStr, ScenarioRunId = scenarioRunId },
            mpPlaced);
        db.OutboxMessages.Add(
            new ProcessOrderCommand { OrderId = orderIdStr, ScenarioRunId = scenarioRunId },
            mpCmd);
        db.OutboxMessages.Add(
            new ReserveStockInternal { OrderId = orderIdStr, ScenarioRunId = scenarioRunId },
            mpRes);
        await db.SaveChangesAsync(cancellationToken);
        return order.Id;
    }
}
