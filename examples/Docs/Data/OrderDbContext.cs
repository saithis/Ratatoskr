using Microsoft.EntityFrameworkCore;
using Ratatoskr.EfCore;

namespace Docs.Data;

#region OrderDbContext
public class OrderDbContext(DbContextOptions<OrderDbContext> options)
    : DbContext(options),
        IRatatoskrDbContext,
        IOutboxDbContext,
        IInboxDbContext
{
    public OutboxStagingCollection OutboxMessages { get; } = new();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddRatatoskr(Database);
    }
}
#endregion
