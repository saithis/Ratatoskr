using Microsoft.EntityFrameworkCore;
using PlaygroundHost.Infrastructure;
using PlaygroundHost.Persistence;
using PlaygroundHost.Persistence.Entities;
using Ratatoskr.Core;

namespace PlaygroundHost.Scenarios.DirectConsume.DirectConsumeSuccess;

public sealed class DirectConsumeSuccessReserveStockInternalHandler(ILogger<DirectConsumeSuccessReserveStockInternalHandler> logger) : IMessageHandler<DirectConsumeSuccessReserveStockInternal>
{
    public Task HandleAsync(DirectConsumeSuccessReserveStockInternal message, MessageProperties properties, CancellationToken cancellationToken)
    {
        logger.LogInformation("ReserveStockInternal processed for order {OrderId}", message.OrderId);
        return Task.CompletedTask;
    }
}

public sealed class DirectConsumeSuccessProcessOrderHandler(ConsumerDbContext db, ILogger<DirectConsumeSuccessProcessOrderHandler> logger) : IMessageHandler<DirectConsumeSuccessProcessOrderCommand>
{
    public async Task HandleAsync(DirectConsumeSuccessProcessOrderCommand message, MessageProperties properties, CancellationToken cancellationToken)
    {
        var orderGuid = Guid.Parse(message.OrderId);
        db.OutboxMessages.Add(
            new DirectConsumeSuccessOrderFulfilled(message.OrderId, message.ScenarioRunId),
            new MessageProperties { Id = PlaygroundMessageIds.OrderFulfilled(orderGuid) });
        await db.SaveChangesAsync(cancellationToken);
    }
}

public sealed class DirectConsumeSuccessOrderFulfilledHandler(PublisherDbContext db, TimeProvider time, ILogger<DirectConsumeSuccessOrderFulfilledHandler> logger)
    : IMessageHandler<DirectConsumeSuccessOrderFulfilled>
{
    public async Task HandleAsync(DirectConsumeSuccessOrderFulfilled message, MessageProperties properties, CancellationToken cancellationToken)
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

public sealed class DirectConsumeSuccessOrderFailedHandler(PublisherDbContext db, TimeProvider time, ILogger<DirectConsumeSuccessOrderFailedHandler> logger)
    : IMessageHandler<DirectConsumeSuccessOrderFailed>
{
    public async Task HandleAsync(DirectConsumeSuccessOrderFailed message, MessageProperties properties, CancellationToken cancellationToken)
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

public sealed class DirectConsumeSuccessOrderPlacedNotifyHandler(ILogger<DirectConsumeSuccessOrderPlacedNotifyHandler> logger) : IMessageHandler<DirectConsumeSuccessOrderPlaced>
{
    public Task HandleAsync(DirectConsumeSuccessOrderPlaced message, MessageProperties properties, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

public sealed class DirectConsumeSuccessOrderPlacedAnalyticsHandler(ILogger<DirectConsumeSuccessOrderPlacedAnalyticsHandler> logger) : IMessageHandler<DirectConsumeSuccessOrderPlaced>
{
    public Task HandleAsync(DirectConsumeSuccessOrderPlaced message, MessageProperties properties, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

public sealed class DirectConsumeSuccessOrderFulfilledNotifyHandler(ILogger<DirectConsumeSuccessOrderFulfilledNotifyHandler> logger) : IMessageHandler<DirectConsumeSuccessOrderFulfilled>
{
    public Task HandleAsync(DirectConsumeSuccessOrderFulfilled message, MessageProperties properties, CancellationToken cancellationToken)
    {
        logger.LogInformation("[Notification] Order fulfilled {OrderId}", message.OrderId);
        return Task.CompletedTask;
    }
}
