using Microsoft.EntityFrameworkCore;
using PlaygroundHost.Infrastructure;
using PlaygroundHost.Persistence;
using PlaygroundHost.Persistence.Entities;
using Ratatoskr.Core;

namespace PlaygroundHost.Scenarios.Inbox.InboxRetryThenSuccess;

public sealed class InboxRetryThenSuccessReserveStockInternalHandler(ILogger<InboxRetryThenSuccessReserveStockInternalHandler> logger) : IMessageHandler<InboxRetryThenSuccessReserveStockInternal>
{
    public Task HandleAsync(InboxRetryThenSuccessReserveStockInternal message, MessageProperties properties, CancellationToken cancellationToken)
    {
        logger.LogInformation("ReserveStockInternal processed for order {OrderId}", message.OrderId);
        return Task.CompletedTask;
    }
}

public sealed class InboxRetryThenSuccessProcessOrderHandler(ConsumerDbContext db, ILogger<InboxRetryThenSuccessProcessOrderHandler> logger) : IMessageHandler<InboxRetryThenSuccessProcessOrderCommand>
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _deliveryAttempts = new();
    public async Task HandleAsync(InboxRetryThenSuccessProcessOrderCommand message, MessageProperties properties, CancellationToken cancellationToken)
    {
        var key = properties.Id ?? message.OrderId;
        var n = _deliveryAttempts.AddOrUpdate(key, 1, (_, old) => old + 1);
        if (n <= 2)
            throw new InvalidOperationException("Simulated consumer failure (succeed-after-2).");
        var orderGuid = Guid.Parse(message.OrderId);
        db.OutboxMessages.Add(
            new InboxRetryThenSuccessOrderFulfilled(message.OrderId, message.ScenarioRunId),
            new MessageProperties { Id = PlaygroundMessageIds.OrderFulfilled(orderGuid) });
        await db.SaveChangesAsync(cancellationToken);
    }
}

public sealed class InboxRetryThenSuccessOrderFulfilledHandler(PublisherDbContext db, TimeProvider time, ILogger<InboxRetryThenSuccessOrderFulfilledHandler> logger)
    : IMessageHandler<InboxRetryThenSuccessOrderFulfilled>
{
    public async Task HandleAsync(InboxRetryThenSuccessOrderFulfilled message, MessageProperties properties, CancellationToken cancellationToken)
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

public sealed class InboxRetryThenSuccessOrderPlacedNotifyHandler(ILogger<InboxRetryThenSuccessOrderPlacedNotifyHandler> logger) : IMessageHandler<InboxRetryThenSuccessOrderPlaced>
{
    public Task HandleAsync(InboxRetryThenSuccessOrderPlaced message, MessageProperties properties, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

public sealed class InboxRetryThenSuccessOrderPlacedAnalyticsHandler(ILogger<InboxRetryThenSuccessOrderPlacedAnalyticsHandler> logger) : IMessageHandler<InboxRetryThenSuccessOrderPlaced>
{
    public Task HandleAsync(InboxRetryThenSuccessOrderPlaced message, MessageProperties properties, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
