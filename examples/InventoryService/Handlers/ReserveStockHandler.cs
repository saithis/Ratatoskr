using InventoryService.Messages;
using InventoryService.Persistence;
using InventoryService.Persistence.Entities;
using Ratatoskr.Core;

namespace InventoryService.Handlers;

public sealed class ReserveStockHandler(
    InventoryDbContext db,
    TimeProvider timeProvider,
    ILogger<ReserveStockHandler> logger
) : IMessageHandler<ReserveStock>
{
    public async Task HandleAsync(
        ReserveStock message,
        MessageProperties properties,
        CancellationToken cancellationToken
    )
    {
        // Deterministic failure switch so the dashboard has something to inspect: the inbox
        // retries this handler until it gives up and marks the row poisoned.
        if (message.Sku.StartsWith("FAIL", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"SKU '{message.Sku}' is flagged as unavailable in the demo warehouse."
            );
        }

        await db.Reservations.AddAsync(
            new StockReservation
            {
                Id = Guid.NewGuid(),
                Sku = message.Sku,
                Quantity = message.Quantity,
                ReservedAt = timeProvider.GetUtcNow(),
            },
            cancellationToken
        );
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Reserved {Quantity} x {Sku}", message.Quantity, message.Sku);
    }
}
