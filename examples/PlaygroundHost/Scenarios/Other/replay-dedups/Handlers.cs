using Microsoft.EntityFrameworkCore;
using PlaygroundHost.Infrastructure;
using PlaygroundHost.Persistence;
using PlaygroundHost.Persistence.Entities;
using Ratatoskr.Core;

namespace PlaygroundHost.Scenarios.Other.ReplayDedups;

public sealed class ReplayDedupsReserveStockInternalHandler(ILogger<ReplayDedupsReserveStockInternalHandler> logger) : IMessageHandler<ReplayDedupsReserveStockInternal>
{
    public Task HandleAsync(ReplayDedupsReserveStockInternal message, MessageProperties properties, CancellationToken cancellationToken)
    {
        logger.LogInformation("ReserveStockInternal processed for order {OrderId}", message.OrderId);
        return Task.CompletedTask;
    }
}

public sealed class ReplayDedupsProcessOrderHandler(ConsumerDbContext db, ILogger<ReplayDedupsProcessOrderHandler> logger) : IMessageHandler<ReplayDedupsProcessOrderCommand>
{
    public async Task HandleAsync(ReplayDedupsProcessOrderCommand message, MessageProperties properties, CancellationToken cancellationToken)
    {
        var orderGuid = Guid.Parse(message.OrderId);
        db.OutboxMessages.Add(
            new ReplayDedupsOrderFulfilled(message.OrderId, message.ScenarioRunId),
            new MessageProperties { Id = PlaygroundMessageIds.OrderFulfilled(orderGuid) });
        await db.SaveChangesAsync(cancellationToken);
    }
}

public sealed class ReplayDedupsOrderFulfilledHandler(PublisherDbContext db, TimeProvider time, ILogger<ReplayDedupsOrderFulfilledHandler> logger)
    : IMessageHandler<ReplayDedupsOrderFulfilled>
{
    public async Task HandleAsync(ReplayDedupsOrderFulfilled message, MessageProperties properties, CancellationToken cancellationToken)
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

public sealed class ReplayDedupsOrderPlacedNotifyHandler(ILogger<ReplayDedupsOrderPlacedNotifyHandler> logger) : IMessageHandler<ReplayDedupsOrderPlaced>
{
    public Task HandleAsync(ReplayDedupsOrderPlaced message, MessageProperties properties, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

public sealed class ReplayDedupsOrderPlacedAnalyticsHandler(ILogger<ReplayDedupsOrderPlacedAnalyticsHandler> logger) : IMessageHandler<ReplayDedupsOrderPlaced>
{
    public Task HandleAsync(ReplayDedupsOrderPlaced message, MessageProperties properties, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
