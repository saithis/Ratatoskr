using Microsoft.EntityFrameworkCore;
using OrderService.Database.Entities;
using Ratatoskr.EfCore;

namespace OrderService.Database;

public class OrdersDbContext(DbContextOptions<OrdersDbContext> options) : DbContext(options), IOutboxDbContext, IInboxDbContext
{
    public DbSet<Order> Orders { get; set; } = null!;
    public OutboxStagingCollection OutboxMessages { get; } = new();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.AddRatatoskrEfCoreModel(Database);
    }
}
