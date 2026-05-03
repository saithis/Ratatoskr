using Microsoft.EntityFrameworkCore;
using PlaygroundHost.Infrastructure;
using PlaygroundHost.Persistence;
using PlaygroundHost.Persistence.Entities;
using Ratatoskr.Core;

namespace PlaygroundHost.Scenarios.Inbox.BusinessRejection;

public sealed class BusinessRejectionReserveStockInternalHandler(ILogger<BusinessRejectionReserveStockInternalHandler> logger) : IMessageHandler<BusinessRejectionReserveStockInternal>
{
    public Task HandleAsync(BusinessRejectionReserveStockInternal message, MessageProperties properties, CancellationToken cancellationToken)
    {
        logger.LogInformation("ReserveStockInternal processed for order {OrderId}", message.OrderId);
        return Task.CompletedTask;
    }
}

public sealed class BusinessRejectionProcessOrderHandler(ConsumerDbContext db, ILogger<BusinessRejectionProcessOrderHandler> logger) : IMessageHandler<BusinessRejectionProcessOrderCommand>
{
    public async Task HandleAsync(BusinessRejectionProcessOrderCommand message, MessageProperties properties, CancellationToken cancellationToken)
    {
        var orderGuid = Guid.Parse(message.OrderId);
        db.OutboxMessages.Add(
            new BusinessRejectionOrderFailed(
                message.OrderId,
                message.ScenarioRunId,
                "Simulated business rejection."),
            new MessageProperties { Id = PlaygroundMessageIds.OrderFailed(orderGuid) });
        await db.SaveChangesAsync(cancellationToken);
        return;
    }
}

public sealed class BusinessRejectionOrderFailedHandler(PublisherDbContext db, TimeProvider time, ILogger<BusinessRejectionOrderFailedHandler> logger)
    : IMessageHandler<BusinessRejectionOrderFailed>
{
    public async Task HandleAsync(BusinessRejectionOrderFailed message, MessageProperties properties, CancellationToken cancellationToken)
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

public sealed class BusinessRejectionOrderPlacedNotifyHandler(ILogger<BusinessRejectionOrderPlacedNotifyHandler> logger) : IMessageHandler<BusinessRejectionOrderPlaced>
{
    public Task HandleAsync(BusinessRejectionOrderPlaced message, MessageProperties properties, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

public sealed class BusinessRejectionOrderPlacedAnalyticsHandler(ILogger<BusinessRejectionOrderPlacedAnalyticsHandler> logger) : IMessageHandler<BusinessRejectionOrderPlaced>
{
    public Task HandleAsync(BusinessRejectionOrderPlaced message, MessageProperties properties, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
