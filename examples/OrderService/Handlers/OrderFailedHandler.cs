using Microsoft.EntityFrameworkCore;
using OrderService.Database;
using OrderService.Database.Entities;
using PlaygroundMessages.Messages;
using Ratatoskr.Core;

namespace OrderService.Handlers;

public class OrderFailedHandler(OrdersDbContext db, ILogger<OrderFailedHandler> logger) : IMessageHandler<OrderFailed>
{
    public async Task HandleAsync(OrderFailed message, MessageProperties properties, CancellationToken cancellationToken)
    {
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == Guid.Parse(message.OrderId), cancellationToken);
        if (order is null)
        {
            logger.LogWarning("Order {OrderId} not found when handling OrderFailed", message.OrderId);
            return;
        }

        order.Status = OrderStatus.Failed;
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Order {OrderId} marked as Failed: {Reason}", message.OrderId, message.Reason);
    }
}
