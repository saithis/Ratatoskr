using Microsoft.EntityFrameworkCore;
using Ratatoskr.EfCore;

namespace Ratatoskr.Tests.Fixtures;

/// <summary>
/// Test DbContext that implements IOutboxDbContext and IInboxDbContext for integration tests.
/// </summary>
public class TestDbContext(DbContextOptions<TestDbContext> options)
    : DbContext(options),
        IOutboxDbContext,
        IInboxDbContext
{
    public DbSet<TestEntity> TestEntities { get; set; }

    public OutboxStagingCollection OutboxMessages { get; } = new();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Configure test entity
        modelBuilder.Entity<TestEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
        });

        modelBuilder.AddRatatoskrEfCoreModel(Database);
    }
}
