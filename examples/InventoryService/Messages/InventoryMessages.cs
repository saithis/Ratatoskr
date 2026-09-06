using Ratatoskr;

namespace InventoryService.Messages;

[RatatoskrMessage("inventory.reserve-stock")]
public record ReserveStock(string Sku, int Quantity);

[RatatoskrMessage("inventory.stock-audited")]
public record StockAudited(string Sku, int Quantity, DateTimeOffset AuditedAt);
