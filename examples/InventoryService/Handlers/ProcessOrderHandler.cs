using InventoryService.Database;
using PlaygroundMessages.Messages;
using Ratatoskr;
using Ratatoskr.Core;

namespace InventoryService.Handlers;

public class ProcessOrderHandler(
    InventoryDemoModeState demoMode,
    IRatatoskr bus,
    ILogger<ProcessOrderHandler> logger) : IMessageHandler<ProcessOrderCommand>
{
    public async Task HandleAsync(ProcessOrderCommand message, MessageProperties properties, CancellationToken cancellationToken)
    {
        switch (demoMode.Mode)
        {
            case InventoryDemoMode.Throw:
                logger.LogWarning("Inventory demo mode Throw — simulating handler failure for order {OrderId}", message.OrderId);
                throw new InvalidOperationException("Simulated inventory failure (demo mode is Throw).");
            case InventoryDemoMode.Reject:
                await bus.PublishDirectAsync(new OrderFailed
                {
                    OrderId = message.OrderId,
                    Reason = "Simulated business rejection (demo mode is Reject).",
                });
                logger.LogInformation("Order {OrderId} rejected (demo mode)", message.OrderId);
                return;
            case InventoryDemoMode.Off:
            default:
                break;
        }

        // PublishDirectAsync bypasses the outbox. There is a small loss window: if this service crashes
        // between publishing and completing the inbox handler, the inbox processor retries the handler,
        // which publishes again with a new CloudEvents id. OrderService's inbox treats it as a new
        // message and processes it (status update is idempotent). This is an acceptable trade-off for
        // keeping this example simpler without an outbox on InventoryService.
        await bus.PublishDirectAsync(new OrderFulfilled { OrderId = message.OrderId });
        logger.LogInformation("Order {OrderId} fulfilled", message.OrderId);
    }
}
