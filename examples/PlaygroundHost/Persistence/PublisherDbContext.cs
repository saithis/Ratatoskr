using Microsoft.EntityFrameworkCore;
using PlaygroundHost.Persistence.Entities;
using Ratatoskr.EfCore;

namespace PlaygroundHost.Persistence;

public class PublisherDbContext(DbContextOptions<PublisherDbContext> options)
    : DbContext(options),
        IOutboxDbContext,
        IInboxDbContext
{
    public DbSet<Order> Orders { get; set; } = null!;
    public OutboxStagingCollection OutboxMessages { get; } = new();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.AddRatatoskrEfCoreModel(Database);
        modelBuilder.Entity<Order>(e =>
        {
            e.Property(o => o.PublishOrigin).HasMaxLength(32).HasDefaultValue("outbox");
        });
    }
}
