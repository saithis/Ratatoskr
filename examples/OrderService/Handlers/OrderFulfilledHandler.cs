using Microsoft.EntityFrameworkCore;
using OrderService.Database;
using OrderService.Database.Entities;
using OrderService.Playground;
using PlaygroundMessages.Messages;
using Ratatoskr.Core;

namespace OrderService.Handlers;

public class OrderFulfilledHandler(
    OrdersDbContext db,
    TimeProvider time,
    OrderConsumePlaygroundState playground,
    ILogger<OrderFulfilledHandler> logger)
    : IMessageHandler<OrderFulfilled>
{
    public async Task HandleAsync(OrderFulfilled message, MessageProperties properties, CancellationToken cancellationToken)
    {
        if (playground.OrderFulfilledHandlerFails)
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
