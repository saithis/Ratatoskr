using Microsoft.EntityFrameworkCore;
using PlaygroundHost.Infrastructure;
using PlaygroundHost.Persistence;
using PlaygroundHost.Persistence.Entities;
using Ratatoskr.Core;

namespace PlaygroundHost.Infrastructure.ScenarioRunning;

/// <summary>Shared helpers for staging publisher orders and correlated outbox payloads.</summary>
public static class PlaygroundScenarioStaging
{
    public static Order AddPlacedOrderToContext(PublisherDbContext db, TimeProvider time, string publishOrigin)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var order = new Order
        {
            Id = Guid.NewGuid(),
            Status = OrderStatus.Placed,
            CreatedAt = now,
            StatusChangedAt = now,
            PublishOrigin = publishOrigin,
        };
        db.Orders.Add(order);
        return order;
    }

    public static void StageCorrelatedOutboxMessage<TMessage>(
        PublisherDbContext db,
        string scenarioRunId,
        TMessage message,
        string cloudEventsMessageId)
        where TMessage : notnull
    {
        var props = new MessageProperties { Id = cloudEventsMessageId };
        PlaygroundCorrelation.AttachToMessageProperties(props, scenarioRunId);
        db.OutboxMessages.Add(message, props);
    }

    public static async Task<Guid> StageOrderWithCommandAsync<TCommand>(
        PublisherDbContext db,
        TimeProvider time,
        string runId,
        Func<string, string, TCommand> buildCommand,
        CancellationToken cancellationToken) where TCommand : notnull
    {
        var order = AddPlacedOrderToContext(db, time, "outbox");
        StageCorrelatedOutboxMessage(
            db,
            runId,
            buildCommand(order.Id.ToString(), runId),
            PlaygroundMessageIds.ProcessOrderCommand(order.Id));
        await db.SaveChangesAsync(cancellationToken);
        return order.Id;
    }

    public static async Task UpdateOrderStatusAsync(
        PublisherDbContext db,
        TimeProvider time,
        string orderId,
        OrderStatus status,
        CancellationToken cancellationToken)
    {
        var order = await db.Orders.FirstOrDefaultAsync(
            o => o.Id == Guid.Parse(orderId), cancellationToken);
        if (order is null) return;
        order.Status = status;
        order.StatusChangedAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
    }
}
