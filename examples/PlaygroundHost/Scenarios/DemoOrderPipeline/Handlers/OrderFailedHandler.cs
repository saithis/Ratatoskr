using Microsoft.EntityFrameworkCore;
using PlaygroundHost.Infrastructure;
using PlaygroundHost.Persistence;
using PlaygroundHost.Persistence.Entities;
using PlaygroundHost.Scenarios.DemoOrderPipeline.Messages;
using Ratatoskr.Core;

namespace PlaygroundHost.Scenarios.DemoOrderPipeline.Handlers;

public class OrderFailedHandler(
    PublisherDbContext db,
    TimeProvider time,
    OrderConsumePlaygroundState playground,
    ILogger<OrderFailedHandler> logger)
    : IMessageHandler<OrderFailed>
{
    public async Task HandleAsync(OrderFailed message, MessageProperties properties, CancellationToken cancellationToken)
    {
        if (playground.TryConsumeOrderFailedFailure())
            throw new InvalidOperationException("Simulated OrderFailed inbox failure (playground toggle).");

        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == Guid.Parse(message.OrderId), cancellationToken);
        if (order is null)
        {
            logger.LogWarning("Order {OrderId} not found when handling OrderFailed", message.OrderId);
            return;
        }

        var now = time.GetUtcNow().UtcDateTime;
        order.Status = OrderStatus.Failed;
        order.StatusChangedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Order {OrderId} marked as Failed: {Reason}", message.OrderId, message.Reason);
    }
}
