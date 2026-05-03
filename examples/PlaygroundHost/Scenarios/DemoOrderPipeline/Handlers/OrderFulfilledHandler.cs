using Microsoft.EntityFrameworkCore;
using PlaygroundHost.Infrastructure;
using PlaygroundHost.Persistence;
using PlaygroundHost.Persistence.Entities;
using PlaygroundHost.Scenarios.DemoOrderPipeline.Messages;
using Ratatoskr.Core;

namespace PlaygroundHost.Scenarios.DemoOrderPipeline.Handlers;

public class OrderFulfilledHandler(
    PublisherDbContext db,
    TimeProvider time,
    OrderConsumePlaygroundState playground,
    ILogger<OrderFulfilledHandler> logger)
    : IMessageHandler<OrderFulfilled>
{
    public async Task HandleAsync(OrderFulfilled message, MessageProperties properties, CancellationToken cancellationToken)
    {
        if (playground.TryConsumeOrderFulfilledFailure())
            throw new InvalidOperationException("Simulated OrderFulfilled inbox failure (playground toggle).");

        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == Guid.Parse(message.OrderId), cancellationToken);
        if (order is null)
        {
            logger.LogWarning("Order {OrderId} not found when handling OrderFulfilled", message.OrderId);
            return;
        }

        var now = time.GetUtcNow().UtcDateTime;
        order.Status = OrderStatus.Fulfilled;
        order.StatusChangedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Order {OrderId} marked as Fulfilled", message.OrderId);
    }
}
