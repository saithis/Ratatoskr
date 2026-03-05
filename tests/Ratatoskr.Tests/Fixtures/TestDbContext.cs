using Microsoft.EntityFrameworkCore;
using Ratatoskr.EfCore;

namespace Ratatoskr.Tests.Fixtures;

/// <summary>
/// Test DbContext that implements IOutboxDbContext and IInboxDbContext for integration tests.
/// </summary>
public class TestDbContext : DbContext, IOutboxDbContext, IInboxDbContext
{
    public TestDbContext(DbContextOptions<TestDbContext> options) : base(options)
    {
    }

    public DbSet<TestEntity> TestEntities { get; set; } = null!;

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

        // Configure outbox entities
        modelBuilder.AddOutboxEntities();

        // Configure inbox entities
        modelBuilder.AddInboxEntities();
    }
}

/// <summary>
/// Second inbox-only DbContext for multi-DbContext integration tests.
/// Uses the same database but a separate DbContext type to verify
/// that the inbox pattern supports multiple DbContexts.
/// </summary>
public class SecondInboxDbContext : DbContext, IInboxDbContext
{
    public SecondInboxDbContext(DbContextOptions<SecondInboxDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.AddInboxEntities();
    }
}

/// <summary>
/// Second outbox DbContext for multi-DbContext outbox cleanup integration tests.
/// Uses the same database but a separate DbContext type to verify
/// that the outbox cleanup scoping by SourceContext works correctly.
/// </summary>
public class SecondOutboxDbContext : DbContext, IOutboxDbContext
{
    public SecondOutboxDbContext(DbContextOptions<SecondOutboxDbContext> options) : base(options)
    {
    }

    public OutboxStagingCollection OutboxMessages { get; } = new();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.AddOutboxEntities();
    }
}
