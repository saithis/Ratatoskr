using Microsoft.EntityFrameworkCore;
using PlaygroundHost.Infrastructure;
using PlaygroundHost.Persistence;
using PlaygroundHost.Persistence.Entities;
using Ratatoskr.Core;

namespace PlaygroundHost.Scenarios.DirectConsume.DirectConsumeDlq;

public sealed class DirectConsumeDlqReserveStockInternalHandler(ILogger<DirectConsumeDlqReserveStockInternalHandler> logger) : IMessageHandler<DirectConsumeDlqReserveStockInternal>
{
    public Task HandleAsync(DirectConsumeDlqReserveStockInternal message, MessageProperties properties, CancellationToken cancellationToken)
    {
        logger.LogInformation("ReserveStockInternal processed for order {OrderId}", message.OrderId);
        return Task.CompletedTask;
    }
}

public sealed class DirectConsumeDlqProcessOrderHandler(ConsumerDbContext db, ILogger<DirectConsumeDlqProcessOrderHandler> logger) : IMessageHandler<DirectConsumeDlqProcessOrderCommand>
{
    public async Task HandleAsync(DirectConsumeDlqProcessOrderCommand message, MessageProperties properties, CancellationToken cancellationToken)
    {
        var orderGuid = Guid.Parse(message.OrderId);
        db.OutboxMessages.Add(
            new DirectConsumeDlqOrderFulfilled(message.OrderId, message.ScenarioRunId),
            new MessageProperties { Id = PlaygroundMessageIds.OrderFulfilled(orderGuid) });
        await db.SaveChangesAsync(cancellationToken);
    }
}

public sealed class DirectConsumeDlqOrderFulfilledHandler(PublisherDbContext db, TimeProvider time, ILogger<DirectConsumeDlqOrderFulfilledHandler> logger)
    : IMessageHandler<DirectConsumeDlqOrderFulfilled>
{
    public async Task HandleAsync(DirectConsumeDlqOrderFulfilled message, MessageProperties properties, CancellationToken cancellationToken)
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

public sealed class DirectConsumeDlqOrderFailedHandler(PublisherDbContext db, TimeProvider time, ILogger<DirectConsumeDlqOrderFailedHandler> logger)
    : IMessageHandler<DirectConsumeDlqOrderFailed>
{
    public async Task HandleAsync(DirectConsumeDlqOrderFailed message, MessageProperties properties, CancellationToken cancellationToken)
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

public sealed class DirectConsumeDlqOrderPlacedNotifyHandler(ILogger<DirectConsumeDlqOrderPlacedNotifyHandler> logger) : IMessageHandler<DirectConsumeDlqOrderPlaced>
{
    public Task HandleAsync(DirectConsumeDlqOrderPlaced message, MessageProperties properties, CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("Simulated OrderPlaced notification failure (DLQ scenario).");
    }
}

public sealed class DirectConsumeDlqOrderPlacedAnalyticsHandler(ILogger<DirectConsumeDlqOrderPlacedAnalyticsHandler> logger) : IMessageHandler<DirectConsumeDlqOrderPlaced>
{
    public Task HandleAsync(DirectConsumeDlqOrderPlaced message, MessageProperties properties, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

public sealed class DirectConsumeDlqOrderFulfilledNotifyHandler(ILogger<DirectConsumeDlqOrderFulfilledNotifyHandler> logger) : IMessageHandler<DirectConsumeDlqOrderFulfilled>
{
    public Task HandleAsync(DirectConsumeDlqOrderFulfilled message, MessageProperties properties, CancellationToken cancellationToken)
    {
        logger.LogInformation("[Notification] Order fulfilled {OrderId}", message.OrderId);
        return Task.CompletedTask;
    }
}
