using Microsoft.EntityFrameworkCore;
using Ratatoskr.EfCore;

namespace InventoryService.Persistence;

public class AuditDbContext(DbContextOptions<AuditDbContext> options)
    : DbContext(options), IOutboxDbContext, IInboxDbContext
{
    public OutboxStagingCollection OutboxMessages { get; } = new();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.AddRatatoskrEfCoreModel(Database);
    }
}
