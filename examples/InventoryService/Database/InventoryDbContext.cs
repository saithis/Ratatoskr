using Microsoft.EntityFrameworkCore;
using Ratatoskr.EfCore;

namespace InventoryService.Database;

// InventoryService uses inbox for deduplication but has no domain tables.
// IOutboxDbContext is required by the AddEfCoreDurability<TDbContext> generic constraint.
public class InventoryDbContext(DbContextOptions<InventoryDbContext> options) : DbContext(options), IOutboxDbContext, IInboxDbContext
{
    public OutboxStagingCollection OutboxMessages { get; } = new();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.AddRatatoskrEfCoreModel(Database);
    }
}
