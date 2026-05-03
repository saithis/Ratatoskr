using Microsoft.EntityFrameworkCore;
using PlaygroundHost.Infrastructure;
using PlaygroundHost.Persistence;
using PlaygroundHost.Persistence.Entities;
using Ratatoskr.Core;

namespace PlaygroundHost.Scenarios.Outbox.OutboxRetryThenSuccess;

public sealed class OutboxRetryThenSuccessReserveStockInternalHandler(ILogger<OutboxRetryThenSuccessReserveStockInternalHandler> logger) : IMessageHandler<OutboxRetryThenSuccessReserveStockInternal>
{
    public Task HandleAsync(OutboxRetryThenSuccessReserveStockInternal message, MessageProperties properties, CancellationToken cancellationToken)
    {
        logger.LogInformation("ReserveStockInternal processed for order {OrderId}", message.OrderId);
        return Task.CompletedTask;
    }
}

public sealed class OutboxRetryThenSuccessProcessOrderHandler(ConsumerDbContext db, ILogger<OutboxRetryThenSuccessProcessOrderHandler> logger) : IMessageHandler<OutboxRetryThenSuccessProcessOrderCommand>
{
    public async Task HandleAsync(OutboxRetryThenSuccessProcessOrderCommand message, MessageProperties properties, CancellationToken cancellationToken)
    {
        var orderGuid = Guid.Parse(message.OrderId);
        db.OutboxMessages.Add(
            new OutboxRetryThenSuccessOrderFulfilled(message.OrderId, message.ScenarioRunId),
            new MessageProperties { Id = PlaygroundMessageIds.OrderFulfilled(orderGuid) });
        await db.SaveChangesAsync(cancellationToken);
    }
}

public sealed class OutboxRetryThenSuccessOrderFulfilledHandler(PublisherDbContext db, TimeProvider time, ILogger<OutboxRetryThenSuccessOrderFulfilledHandler> logger)
    : IMessageHandler<OutboxRetryThenSuccessOrderFulfilled>
{
    public async Task HandleAsync(OutboxRetryThenSuccessOrderFulfilled message, MessageProperties properties, CancellationToken cancellationToken)
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

public sealed class OutboxRetryThenSuccessOrderPlacedNotifyHandler(ILogger<OutboxRetryThenSuccessOrderPlacedNotifyHandler> logger) : IMessageHandler<OutboxRetryThenSuccessOrderPlaced>
{
    public Task HandleAsync(OutboxRetryThenSuccessOrderPlaced message, MessageProperties properties, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

public sealed class OutboxRetryThenSuccessOrderPlacedAnalyticsHandler(ILogger<OutboxRetryThenSuccessOrderPlacedAnalyticsHandler> logger) : IMessageHandler<OutboxRetryThenSuccessOrderPlaced>
{
    public Task HandleAsync(OutboxRetryThenSuccessOrderPlaced message, MessageProperties properties, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
