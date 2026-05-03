using Microsoft.EntityFrameworkCore;
using PlaygroundHost.Infrastructure;
using PlaygroundHost.Persistence;
using PlaygroundHost.Persistence.Entities;
using Ratatoskr.Core;

namespace PlaygroundHost.Scenarios.Outbox.OutboxPoison;

public sealed class OutboxPoisonReserveStockInternalHandler(ILogger<OutboxPoisonReserveStockInternalHandler> logger) : IMessageHandler<OutboxPoisonReserveStockInternal>
{
    public Task HandleAsync(OutboxPoisonReserveStockInternal message, MessageProperties properties, CancellationToken cancellationToken)
    {
        logger.LogInformation("ReserveStockInternal processed for order {OrderId}", message.OrderId);
        return Task.CompletedTask;
    }
}

public sealed class OutboxPoisonProcessOrderHandler(ConsumerDbContext db, ILogger<OutboxPoisonProcessOrderHandler> logger) : IMessageHandler<OutboxPoisonProcessOrderCommand>
{
    public async Task HandleAsync(OutboxPoisonProcessOrderCommand message, MessageProperties properties, CancellationToken cancellationToken)
    {
        var orderGuid = Guid.Parse(message.OrderId);
        db.OutboxMessages.Add(
            new OutboxPoisonOrderFulfilled(message.OrderId, message.ScenarioRunId),
            new MessageProperties { Id = PlaygroundMessageIds.OrderFulfilled(orderGuid) });
        await db.SaveChangesAsync(cancellationToken);
    }
}

public sealed class OutboxPoisonOrderFulfilledHandler(PublisherDbContext db, TimeProvider time, ILogger<OutboxPoisonOrderFulfilledHandler> logger)
    : IMessageHandler<OutboxPoisonOrderFulfilled>
{
    public async Task HandleAsync(OutboxPoisonOrderFulfilled message, MessageProperties properties, CancellationToken cancellationToken)
    {
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == Guid.Parse(message.OrderId), cancellationToken);
        if (order is null) return;
        var now = time.GetUtcNow().UtcDateTime;
        order.Status = OrderStatus.Fulfilled;
        order.StatusChangedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Order {OrderId} marked Fulfilled", message.OrderId);
    }
}

public sealed class OutboxPoisonOrderFailedHandler(PublisherDbContext db, TimeProvider time, ILogger<OutboxPoisonOrderFailedHandler> logger)
    : IMessageHandler<OutboxPoisonOrderFailed>
{
    public async Task HandleAsync(OutboxPoisonOrderFailed message, MessageProperties properties, CancellationToken cancellationToken)
    {
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == Guid.Parse(message.OrderId), cancellationToken);
        if (order is null) return;
        var now = time.GetUtcNow().UtcDateTime;
        order.Status = OrderStatus.Failed;
        order.StatusChangedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Order {OrderId} marked Failed", message.OrderId);
    }
}

public sealed class OutboxPoisonOrderPlacedNotifyHandler(ILogger<OutboxPoisonOrderPlacedNotifyHandler> logger) : IMessageHandler<OutboxPoisonOrderPlaced>
{
    public Task HandleAsync(OutboxPoisonOrderPlaced message, MessageProperties properties, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

public sealed class OutboxPoisonOrderPlacedAnalyticsHandler(ILogger<OutboxPoisonOrderPlacedAnalyticsHandler> logger) : IMessageHandler<OutboxPoisonOrderPlaced>
{
    public Task HandleAsync(OutboxPoisonOrderPlaced message, MessageProperties properties, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

public sealed class OutboxPoisonOrderFulfilledNotifyHandler(ILogger<OutboxPoisonOrderFulfilledNotifyHandler> logger) : IMessageHandler<OutboxPoisonOrderFulfilled>
{
    public Task HandleAsync(OutboxPoisonOrderFulfilled message, MessageProperties properties, CancellationToken cancellationToken)
    {
        logger.LogInformation("[Notification] Order fulfilled {OrderId}", message.OrderId);
        return Task.CompletedTask;
    }
}
