using PlaygroundMessages.Messages;
using Ratatoskr.Core;

namespace OrderService.Handlers;

public class ReserveStockInternalHandler(ILogger<ReserveStockInternalHandler> logger) : IMessageHandler<ReserveStockInternal>
{
    public Task HandleAsync(ReserveStockInternal message, MessageProperties properties, CancellationToken cancellationToken)
    {
        logger.LogInformation("ReserveStockInternal processed for order {OrderId}", message.OrderId);
        return Task.CompletedTask;
    }
}
