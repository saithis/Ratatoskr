using Microsoft.EntityFrameworkCore;
using PlaygroundHost.Infrastructure;
using PlaygroundHost.Persistence;
using PlaygroundHost.Persistence.Entities;
using Ratatoskr.Core;

namespace PlaygroundHost.Scenarios.DirectConsume.DirectConsumeRetry;

public sealed class DirectConsumeRetryReserveStockInternalHandler(ILogger<DirectConsumeRetryReserveStockInternalHandler> logger) : IMessageHandler<DirectConsumeRetryReserveStockInternal>
{
    public Task HandleAsync(DirectConsumeRetryReserveStockInternal message, MessageProperties properties, CancellationToken cancellationToken)
    {
        logger.LogInformation("ReserveStockInternal processed for order {OrderId}", message.OrderId);
        return Task.CompletedTask;
    }
}

public sealed class DirectConsumeRetryProcessOrderHandler(ConsumerDbContext db, ILogger<DirectConsumeRetryProcessOrderHandler> logger) : IMessageHandler<DirectConsumeRetryProcessOrderCommand>
{
    public async Task HandleAsync(DirectConsumeRetryProcessOrderCommand message, MessageProperties properties, CancellationToken cancellationToken)
    {
        var orderGuid = Guid.Parse(message.OrderId);
        db.OutboxMessages.Add(
            new DirectConsumeRetryOrderFulfilled(message.OrderId, message.ScenarioRunId),
            new MessageProperties { Id = PlaygroundMessageIds.OrderFulfilled(orderGuid) });
        await db.SaveChangesAsync(cancellationToken);
    }
}

public sealed class DirectConsumeRetryOrderFulfilledHandler(PublisherDbContext db, TimeProvider time, ILogger<DirectConsumeRetryOrderFulfilledHandler> logger)
    : IMessageHandler<DirectConsumeRetryOrderFulfilled>
{
    public async Task HandleAsync(DirectConsumeRetryOrderFulfilled message, MessageProperties properties, CancellationToken cancellationToken)
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

public sealed class DirectConsumeRetryOrderPlacedNotifyHandler(ILogger<DirectConsumeRetryOrderPlacedNotifyHandler> logger) : IMessageHandler<DirectConsumeRetryOrderPlaced>
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _notifyFails = new();
    public Task HandleAsync(DirectConsumeRetryOrderPlaced message, MessageProperties properties, CancellationToken cancellationToken)
    {
        var key = properties.Id ?? message.OrderId;
        var n = _notifyFails.AddOrUpdate(key, 1, (_, old) => old + 1);
        if (n <= 2)
            throw new InvalidOperationException("Simulated OrderPlaced notification failure (succeed-after-2).");
        return Task.CompletedTask;
    }
}

public sealed class DirectConsumeRetryOrderPlacedAnalyticsHandler(ILogger<DirectConsumeRetryOrderPlacedAnalyticsHandler> logger) : IMessageHandler<DirectConsumeRetryOrderPlaced>
{
    public Task HandleAsync(DirectConsumeRetryOrderPlaced message, MessageProperties properties, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
