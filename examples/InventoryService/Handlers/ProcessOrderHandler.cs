using InventoryService.Database;
using PlaygroundMessages.Messages;
using Ratatoskr;
using Ratatoskr.Core;

namespace InventoryService.Handlers;

public class ProcessOrderHandler(
    FailureModeState failureMode,
    IRatatoskr bus,
    ILogger<ProcessOrderHandler> logger) : IMessageHandler<ProcessOrderCommand>
{
    public async Task HandleAsync(ProcessOrderCommand message, MessageProperties properties, CancellationToken cancellationToken)
    {
        if (failureMode.IsEnabled)
        {
            logger.LogWarning("Failure mode is ON — throwing to trigger retry for order {OrderId}", message.OrderId);
            throw new InvalidOperationException("Simulated inventory failure (failure mode is ON).");
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
