namespace InventoryService.Persistence.Entities;

public sealed class StockReservation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Sku { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public DateTimeOffset ReservedAt { get; set; } = DateTimeOffset.UtcNow;
}
