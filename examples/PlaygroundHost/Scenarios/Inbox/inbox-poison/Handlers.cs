using Microsoft.EntityFrameworkCore;
using PlaygroundHost.Infrastructure;
using PlaygroundHost.Persistence;
using PlaygroundHost.Persistence.Entities;
using Ratatoskr.Core;

namespace PlaygroundHost.Scenarios.Inbox.InboxPoison;

public sealed class InboxPoisonReserveStockInternalHandler(ILogger<InboxPoisonReserveStockInternalHandler> logger)
    : IMessageHandler<InboxPoisonReserveStockInternal>
{
    public Task HandleAsync(InboxPoisonReserveStockInternal message, MessageProperties properties, CancellationToken cancellationToken)
    {
        logger.LogInformation("ReserveStockInternal processed for order {OrderId}", message.OrderId);
        return Task.CompletedTask;
    }
}

public sealed class InboxPoisonProcessOrderHandler(ILogger<InboxPoisonProcessOrderHandler> logger)
    : IMessageHandler<InboxPoisonProcessOrderCommand>
{
    public Task HandleAsync(InboxPoisonProcessOrderCommand message, MessageProperties properties, CancellationToken cancellationToken) =>
        Task.FromException(new InvalidOperationException("Simulated inventory inbox failure (poison scenario)."));
}

public sealed class InboxPoisonOrderFulfilledHandler(PublisherDbContext db, TimeProvider time, ILogger<InboxPoisonOrderFulfilledHandler> logger)
    : IMessageHandler<InboxPoisonOrderFulfilled>
{
    public async Task HandleAsync(InboxPoisonOrderFulfilled message, MessageProperties properties, CancellationToken cancellationToken)
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

public sealed class InboxPoisonOrderFailedHandler(PublisherDbContext db, TimeProvider time, ILogger<InboxPoisonOrderFailedHandler> logger)
    : IMessageHandler<InboxPoisonOrderFailed>
{
    public async Task HandleAsync(InboxPoisonOrderFailed message, MessageProperties properties, CancellationToken cancellationToken)
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

public sealed class InboxPoisonOrderPlacedNotifyHandler(ILogger<InboxPoisonOrderPlacedNotifyHandler> logger) : IMessageHandler<InboxPoisonOrderPlaced>
{
    public Task HandleAsync(InboxPoisonOrderPlaced message, MessageProperties properties, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

public sealed class InboxPoisonOrderPlacedAnalyticsHandler(ILogger<InboxPoisonOrderPlacedAnalyticsHandler> logger) : IMessageHandler<InboxPoisonOrderPlaced>
{
    public Task HandleAsync(InboxPoisonOrderPlaced message, MessageProperties properties, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

public sealed class InboxPoisonOrderFulfilledNotifyHandler(ILogger<InboxPoisonOrderFulfilledNotifyHandler> logger) : IMessageHandler<InboxPoisonOrderFulfilled>
{
    public Task HandleAsync(InboxPoisonOrderFulfilled message, MessageProperties properties, CancellationToken cancellationToken)
    {
        logger.LogInformation("[Notification] Order fulfilled {OrderId}", message.OrderId);
        return Task.CompletedTask;
    }
}
