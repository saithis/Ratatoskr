using Ratatoskr;

namespace InventoryService.Messages;

/// <summary>
/// Command delivered through the EF Core transport into <c>InventoryDbContext</c>'s inbox.
/// A SKU starting with <c>FAIL</c> makes the handler throw so the dashboard has poisoned inbox
/// rows to work with.
/// </summary>
[RatatoskrMessage("inventory.reserve-stock")]
public sealed record ReserveStock(string Sku, int Quantity);

/// <summary>
/// Audit event staged in <c>AuditDbContext</c>'s outbox and delivered to
/// <c>InventoryDbContext</c>'s inbox, which is the cross-DbContext EF Core transport path.
/// </summary>
[RatatoskrMessage("inventory.stock-audited")]
public sealed record StockAudited(string Sku, int Quantity, DateTimeOffset RecordedAt);
