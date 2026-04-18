using Microsoft.EntityFrameworkCore;
using Ratatoskr.EfCore;

namespace OrderService.Data;

public class OrdersDbContext(DbContextOptions<OrdersDbContext> options)
    : DbContext(options), IOutboxDbContext, IInboxDbContext
{
    public OutboxStagingCollection OutboxMessages { get; } = new();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.AddRatatoskrEfCoreModel(Database);
    }
}
