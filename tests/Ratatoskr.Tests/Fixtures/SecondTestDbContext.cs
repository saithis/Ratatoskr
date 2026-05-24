using Microsoft.EntityFrameworkCore;
using Ratatoskr.EfCore;

namespace Ratatoskr.Tests.Fixtures;

/// <summary>
/// Second DbContext for multi-DbContext integration tests.
/// Represents a separate bounded context (e.g., shipping) that has its own inbox/outbox tables.
/// </summary>
public class SecondTestDbContext(DbContextOptions<SecondTestDbContext> options)
    : DbContext(options),
        IOutboxDbContext,
        IInboxDbContext
{
    public DbSet<TestEntity> TestEntities { get; set; }

    public OutboxStagingCollection OutboxMessages { get; } = new();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<TestEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
        });

        modelBuilder.AddRatatoskrEfCoreModel(Database);
    }
}
