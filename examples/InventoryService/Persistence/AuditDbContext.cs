using Microsoft.EntityFrameworkCore;
using Ratatoskr.EfCore;

namespace InventoryService.Persistence;

/// <summary>
/// Second store of the inventory service, registered with <c>UseOutbox()</c> only. It exists to
/// show the dashboard handling a DbContext that has just one half configured: the workbench
/// disables the Inbox toggle and the DbContext card reads "Inbox: not configured".
/// </summary>
public class AuditDbContext(DbContextOptions<AuditDbContext> options)
    : DbContext(options),
        IOutboxDbContext,
        IInboxDbContext
{
    public OutboxStagingCollection OutboxMessages { get; } = new();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.AddRatatoskrEfCoreModel(Database);
    }
}
