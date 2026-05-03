using Microsoft.EntityFrameworkCore;
using OrderService.Database;
using OrderService.Database.Entities;
using PlaygroundMessages.Messages;
using Ratatoskr.Core;

namespace OrderService.Handlers;

public class OrderFailedHandler(OrdersDbContext db, TimeProvider time, ILogger<OrderFailedHandler> logger)
    : IMessageHandler<OrderFailed>
{
    public async Task HandleAsync(OrderFailed message, MessageProperties properties, CancellationToken cancellationToken)
    {
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
