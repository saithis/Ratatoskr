using Microsoft.EntityFrameworkCore;
using Ratatoskr.EfCore;

namespace Docs.Data;

#region OrderDbContext
public class OrderDbContext(DbContextOptions<OrderDbContext> options)
    : DbContext(options),
        IRatatoskrDbContext
{
    public OutboxStagingCollection Outbox { get; } = new();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddRatatoskr(Database);
    }
}
#endregion
