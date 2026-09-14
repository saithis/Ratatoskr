using InventoryService.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Ratatoskr.EfCore;

namespace InventoryService.Persistence;

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
        modelBuilder.AddRatatoskrEfCoreModel(Database);
        modelBuilder.Entity<StockReservation>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.Sku).HasMaxLength(64).IsRequired();
        });
    }
}
