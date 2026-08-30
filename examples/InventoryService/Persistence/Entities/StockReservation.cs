namespace InventoryService.Persistence.Entities;

/// <summary>Business data written by the inbox handler in the same transaction as the inbox row.</summary>
public class StockReservation
{
    public Guid Id { get; set; }
    public string Sku { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public DateTimeOffset ReservedAt { get; set; }
}
