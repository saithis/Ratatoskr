using Microsoft.EntityFrameworkCore;
using Ratatoskr.EfCore;

namespace Docs.Data;

#region OrderDbContext
public class OrderDbContext(DbContextOptions<OrderDbContext> options)
    : DbContext(options),
        IOutboxDbContext,
        IInboxDbContext
{
    public OutboxStagingCollection OutboxMessages { get; } = new();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddRatatoskrEfCoreModel(Database);
    }
}
#endregion
