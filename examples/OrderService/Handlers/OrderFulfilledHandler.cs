using Microsoft.EntityFrameworkCore;
using OrderService.Database;
using OrderService.Database.Entities;
using PlaygroundMessages.Messages;
using Ratatoskr.Core;

namespace OrderService.Handlers;

public class OrderFulfilledHandler(OrdersDbContext db, ILogger<OrderFulfilledHandler> logger) : IMessageHandler<OrderFulfilled>
{
    public async Task HandleAsync(OrderFulfilled message, MessageProperties properties, CancellationToken cancellationToken)
    {
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == Guid.Parse(message.OrderId), cancellationToken);
        if (order is null)
        {
            logger.LogWarning("Order {OrderId} not found when handling OrderFulfilled", message.OrderId);
            return;
        }

        order.Status = OrderStatus.Fulfilled;
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Order {OrderId} marked as Fulfilled", message.OrderId);
    }
}
