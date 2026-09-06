using System.Globalization;
using InventoryService.Messages;
using InventoryService.Persistence;
using InventoryService.Persistence.Entities;
using Ratatoskr;
using Ratatoskr.Core;

namespace InventoryService.Handlers;

public sealed class ReserveStockHandler(
    InventoryDbContext inventoryDb,
    AuditDbContext auditDb,
    TimeProvider timeProvider
) : IMessageHandler<ReserveStock>
{
    public async Task HandleAsync(
        ReserveStock message,
        MessageProperties properties,
        CancellationToken cancellationToken
    )
    {
        // Fail if SKU starts with "FAIL" to easily demonstrate inbox retry and poison in the Management UI
        if (message.Sku.StartsWith("FAIL", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Simulated stock reservation failure for SKU '{message.Sku}'! (Quantity: {message.Quantity.ToString(CultureInfo.InvariantCulture)})"
            );
        }

        var reservation = new StockReservation
        {
            Id = Guid.NewGuid(),
            Sku = message.Sku,
            Quantity = message.Quantity,
            ReservedAt = timeProvider.GetUtcNow(),
        };

        await inventoryDb.Reservations.AddAsync(reservation, cancellationToken);
        await inventoryDb.SaveChangesAsync(cancellationToken);

        // Stage audit event in AuditDbContext outbox
        auditDb.OutboxMessages.Add(new StockAudited(message.Sku, message.Quantity, timeProvider.GetUtcNow()));
        await auditDb.SaveChangesAsync(cancellationToken);
    }
}
