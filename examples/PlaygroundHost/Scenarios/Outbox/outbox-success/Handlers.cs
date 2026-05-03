using Microsoft.EntityFrameworkCore;
using PlaygroundHost.Infrastructure;
using PlaygroundHost.Persistence;
using PlaygroundHost.Persistence.Entities;
using Ratatoskr.Core;

namespace PlaygroundHost.Scenarios.Outbox.OutboxSuccess;

public sealed class OutboxSuccessReserveStockInternalHandler(ILogger<OutboxSuccessReserveStockInternalHandler> logger) : IMessageHandler<OutboxSuccessReserveStockInternal>
{
    public Task HandleAsync(OutboxSuccessReserveStockInternal message, MessageProperties properties, CancellationToken cancellationToken)
    {
        logger.LogInformation("ReserveStockInternal processed for order {OrderId}", message.OrderId);
        return Task.CompletedTask;
    }
}

public sealed class OutboxSuccessProcessOrderHandler(ConsumerDbContext db, ILogger<OutboxSuccessProcessOrderHandler> logger) : IMessageHandler<OutboxSuccessProcessOrderCommand>
{
    public async Task HandleAsync(OutboxSuccessProcessOrderCommand message, MessageProperties properties, CancellationToken cancellationToken)
    {
        var orderGuid = Guid.Parse(message.OrderId);
        db.OutboxMessages.Add(
            new OutboxSuccessOrderFulfilled(message.OrderId, message.ScenarioRunId),
            new MessageProperties { Id = PlaygroundMessageIds.OrderFulfilled(orderGuid) });
        await db.SaveChangesAsync(cancellationToken);
    }
}

public sealed class OutboxSuccessOrderFulfilledHandler(PublisherDbContext db, TimeProvider time, ILogger<OutboxSuccessOrderFulfilledHandler> logger)
    : IMessageHandler<OutboxSuccessOrderFulfilled>
{
    public async Task HandleAsync(OutboxSuccessOrderFulfilled message, MessageProperties properties, CancellationToken cancellationToken)
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

public sealed class OutboxSuccessOrderFailedHandler(PublisherDbContext db, TimeProvider time, ILogger<OutboxSuccessOrderFailedHandler> logger)
    : IMessageHandler<OutboxSuccessOrderFailed>
{
    public async Task HandleAsync(OutboxSuccessOrderFailed message, MessageProperties properties, CancellationToken cancellationToken)
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

public sealed class OutboxSuccessOrderPlacedNotifyHandler(ILogger<OutboxSuccessOrderPlacedNotifyHandler> logger) : IMessageHandler<OutboxSuccessOrderPlaced>
{
    public Task HandleAsync(OutboxSuccessOrderPlaced message, MessageProperties properties, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

public sealed class OutboxSuccessOrderPlacedAnalyticsHandler(ILogger<OutboxSuccessOrderPlacedAnalyticsHandler> logger) : IMessageHandler<OutboxSuccessOrderPlaced>
{
    public Task HandleAsync(OutboxSuccessOrderPlaced message, MessageProperties properties, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

public sealed class OutboxSuccessOrderFulfilledNotifyHandler(ILogger<OutboxSuccessOrderFulfilledNotifyHandler> logger) : IMessageHandler<OutboxSuccessOrderFulfilled>
{
    public Task HandleAsync(OutboxSuccessOrderFulfilled message, MessageProperties properties, CancellationToken cancellationToken)
    {
        logger.LogInformation("[Notification] Order fulfilled {OrderId}", message.OrderId);
        return Task.CompletedTask;
    }
}
