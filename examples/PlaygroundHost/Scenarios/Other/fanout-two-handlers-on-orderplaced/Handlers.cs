using Microsoft.EntityFrameworkCore;
using PlaygroundHost.Infrastructure;
using PlaygroundHost.Persistence;
using PlaygroundHost.Persistence.Entities;
using Ratatoskr.Core;

namespace PlaygroundHost.Scenarios.Other.FanoutTwoHandlersOnOrderplaced;

public sealed class FanoutTwoHandlersOnOrderplacedReserveStockInternalHandler(ILogger<FanoutTwoHandlersOnOrderplacedReserveStockInternalHandler> logger) : IMessageHandler<FanoutTwoHandlersOnOrderplacedReserveStockInternal>
{
    public Task HandleAsync(FanoutTwoHandlersOnOrderplacedReserveStockInternal message, MessageProperties properties, CancellationToken cancellationToken)
    {
        logger.LogInformation("ReserveStockInternal processed for order {OrderId}", message.OrderId);
        return Task.CompletedTask;
    }
}

public sealed class FanoutTwoHandlersOnOrderplacedProcessOrderHandler(ConsumerDbContext db, ILogger<FanoutTwoHandlersOnOrderplacedProcessOrderHandler> logger) : IMessageHandler<FanoutTwoHandlersOnOrderplacedProcessOrderCommand>
{
    public async Task HandleAsync(FanoutTwoHandlersOnOrderplacedProcessOrderCommand message, MessageProperties properties, CancellationToken cancellationToken)
    {
        var orderGuid = Guid.Parse(message.OrderId);
        db.OutboxMessages.Add(
            new FanoutTwoHandlersOnOrderplacedOrderFulfilled(message.OrderId, message.ScenarioRunId),
            new MessageProperties { Id = PlaygroundMessageIds.OrderFulfilled(orderGuid) });
        await db.SaveChangesAsync(cancellationToken);
    }
}

public sealed class FanoutTwoHandlersOnOrderplacedOrderFulfilledHandler(PublisherDbContext db, TimeProvider time, ILogger<FanoutTwoHandlersOnOrderplacedOrderFulfilledHandler> logger)
    : IMessageHandler<FanoutTwoHandlersOnOrderplacedOrderFulfilled>
{
    public async Task HandleAsync(FanoutTwoHandlersOnOrderplacedOrderFulfilled message, MessageProperties properties, CancellationToken cancellationToken)
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

public sealed class FanoutTwoHandlersOnOrderplacedOrderPlacedNotifyHandler(ILogger<FanoutTwoHandlersOnOrderplacedOrderPlacedNotifyHandler> logger) : IMessageHandler<FanoutTwoHandlersOnOrderplacedOrderPlaced>
{
    public Task HandleAsync(FanoutTwoHandlersOnOrderplacedOrderPlaced message, MessageProperties properties, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

public sealed class FanoutTwoHandlersOnOrderplacedOrderPlacedAnalyticsHandler(ILogger<FanoutTwoHandlersOnOrderplacedOrderPlacedAnalyticsHandler> logger) : IMessageHandler<FanoutTwoHandlersOnOrderplacedOrderPlaced>
{
    public Task HandleAsync(FanoutTwoHandlersOnOrderplacedOrderPlaced message, MessageProperties properties, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
