using Microsoft.EntityFrameworkCore;
using Ratatoskr.EfCore;

namespace PlaygroundHost.Persistence;

/// <summary>Consumer-side durability: command inbox and outcome outbox.</summary>
public class ConsumerDbContext(DbContextOptions<ConsumerDbContext> options)
    : DbContext(options),
        IRatatoskrDbContext,
        IOutboxDbContext,
        IInboxDbContext
{
    public OutboxStagingCollection OutboxMessages { get; } = new();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.AddRatatoskr(Database);
    }
}
