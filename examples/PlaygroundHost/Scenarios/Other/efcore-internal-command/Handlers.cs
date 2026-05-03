using Microsoft.EntityFrameworkCore;
using PlaygroundHost.Infrastructure;
using PlaygroundHost.Persistence;
using PlaygroundHost.Persistence.Entities;
using Ratatoskr.Core;

namespace PlaygroundHost.Scenarios.Other.EfcoreInternalCommand;

public sealed class EfcoreInternalCommandReserveStockInternalHandler(ILogger<EfcoreInternalCommandReserveStockInternalHandler> logger) : IMessageHandler<EfcoreInternalCommandReserveStockInternal>
{
    public Task HandleAsync(EfcoreInternalCommandReserveStockInternal message, MessageProperties properties, CancellationToken cancellationToken)
    {
        logger.LogInformation("ReserveStockInternal processed for order {OrderId}", message.OrderId);
        return Task.CompletedTask;
    }
}

public sealed class EfcoreInternalCommandProcessOrderHandler(ConsumerDbContext db, ILogger<EfcoreInternalCommandProcessOrderHandler> logger) : IMessageHandler<EfcoreInternalCommandProcessOrderCommand>
{
    public async Task HandleAsync(EfcoreInternalCommandProcessOrderCommand message, MessageProperties properties, CancellationToken cancellationToken)
    {
        var orderGuid = Guid.Parse(message.OrderId);
        db.OutboxMessages.Add(
            new EfcoreInternalCommandOrderFulfilled(message.OrderId, message.ScenarioRunId),
            new MessageProperties { Id = PlaygroundMessageIds.OrderFulfilled(orderGuid) });
        await db.SaveChangesAsync(cancellationToken);
    }
}

public sealed class EfcoreInternalCommandOrderFulfilledHandler(PublisherDbContext db, TimeProvider time, ILogger<EfcoreInternalCommandOrderFulfilledHandler> logger)
    : IMessageHandler<EfcoreInternalCommandOrderFulfilled>
{
    public async Task HandleAsync(EfcoreInternalCommandOrderFulfilled message, MessageProperties properties, CancellationToken cancellationToken)
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

public sealed class EfcoreInternalCommandOrderPlacedNotifyHandler(ILogger<EfcoreInternalCommandOrderPlacedNotifyHandler> logger) : IMessageHandler<EfcoreInternalCommandOrderPlaced>
{
    public Task HandleAsync(EfcoreInternalCommandOrderPlaced message, MessageProperties properties, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

public sealed class EfcoreInternalCommandOrderPlacedAnalyticsHandler(ILogger<EfcoreInternalCommandOrderPlacedAnalyticsHandler> logger) : IMessageHandler<EfcoreInternalCommandOrderPlaced>
{
    public Task HandleAsync(EfcoreInternalCommandOrderPlaced message, MessageProperties properties, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
