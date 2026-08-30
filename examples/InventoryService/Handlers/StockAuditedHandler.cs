using InventoryService.Messages;
using Ratatoskr.Core;

namespace InventoryService.Handlers;

public sealed class StockAuditedHandler(ILogger<StockAuditedHandler> logger)
    : IMessageHandler<StockAudited>
{
    public Task HandleAsync(
        StockAudited message,
        MessageProperties properties,
        CancellationToken cancellationToken
    )
    {
        logger.LogInformation(
            "Audited {Quantity} x {Sku} recorded at {RecordedAt}",
            message.Quantity,
            message.Sku,
            message.RecordedAt
        );
        return Task.CompletedTask;
    }
}
