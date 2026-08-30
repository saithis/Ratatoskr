using InventoryService.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Ratatoskr.EfCore;

namespace InventoryService.Persistence;

/// <summary>
/// Primary store of the inventory service: reservations plus a configured outbox and inbox.
/// The dashboard shows both halves as available for this context.
/// </summary>
public class InventoryDbContext(DbContextOptions<InventoryDbContext> options)
    : DbContext(options),
        IOutboxDbContext,
        IInboxDbContext
{
    public DbSet<StockReservation> Reservations { get; set; } = null!;

    public OutboxStagingCollection OutboxMessages { get; } = new();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<StockReservation>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.Sku).IsRequired().HasMaxLength(64);
        });
        modelBuilder.AddRatatoskrEfCoreModel(Database);
    }
}
