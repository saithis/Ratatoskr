using InventoryService.Messages;
using Microsoft.Extensions.Logging;
using Ratatoskr;
using Ratatoskr.Core;

namespace InventoryService.Handlers;

public sealed class StockAuditedHandler(ILogger<StockAuditedHandler> logger) : IMessageHandler<StockAudited>
{
    public Task HandleAsync(
        StockAudited message,
        MessageProperties properties,
        CancellationToken cancellationToken
    )
    {
        logger.LogInformation("Stock audited: {Quantity}x {Sku} at {AuditedAt}", message.Quantity, message.Sku, message.AuditedAt);
        return Task.CompletedTask;
    }
}
