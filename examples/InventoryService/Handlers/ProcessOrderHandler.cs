using InventoryService.Database;
using PlaygroundMessages;
using PlaygroundMessages.Messages;
using Ratatoskr.Core;

namespace InventoryService.Handlers;

public class ProcessOrderHandler(
    InventoryDemoModeState demoMode,
    InventoryDbContext db,
    ILogger<ProcessOrderHandler> logger) : IMessageHandler<ProcessOrderCommand>
{
    public async Task HandleAsync(ProcessOrderCommand message, MessageProperties properties, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(message.OrderId, out var orderGuid))
            throw new InvalidOperationException($"Order id '{message.OrderId}' is not a GUID.");

        if (demoMode.TryConsumeProcessFailure())
        {
            logger.LogWarning("Inventory demo mode simulating handler failure for order {OrderId}", message.OrderId);
            throw new InvalidOperationException("Simulated inventory failure (demo mode).");
        }

        switch (demoMode.Mode)
        {
            case InventoryDemoMode.Reject:
                db.OutboxMessages.Add(
                    new OrderFailed
                    {
                        OrderId = message.OrderId,
                        Reason = "Simulated business rejection (demo mode is Reject).",
                    },
                    new MessageProperties { Id = PlaygroundMessageIds.OrderFailed(orderGuid) });
                await db.SaveChangesAsync(cancellationToken);
                logger.LogInformation("Order {OrderId} rejected (demo mode)", message.OrderId);
                return;
            case InventoryDemoMode.Off:
            case InventoryDemoMode.Throw:
            case InventoryDemoMode.SucceedAfter:
            default:
                db.OutboxMessages.Add(
                    new OrderFulfilled { OrderId = message.OrderId },
                    new MessageProperties { Id = PlaygroundMessageIds.OrderFulfilled(orderGuid) });
                await db.SaveChangesAsync(cancellationToken);
                logger.LogInformation("Order {OrderId} fulfilled", message.OrderId);
                return;
        }
    }
}
