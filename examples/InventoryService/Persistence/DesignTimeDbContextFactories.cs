using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace InventoryService.Persistence;

public sealed class InventoryDbContextFactory : IDesignTimeDbContextFactory<InventoryDbContext>
{
    public InventoryDbContext CreateDbContext(string[] args) =>
        new(
            new DbContextOptionsBuilder<InventoryDbContext>()
                .UseNpgsql("Host=localhost;Database=inventorydb")
                .Options
        );
}

public sealed class AuditDbContextFactory : IDesignTimeDbContextFactory<AuditDbContext>
{
    public AuditDbContext CreateDbContext(string[] args) =>
        new(
            new DbContextOptionsBuilder<AuditDbContext>()
                .UseNpgsql("Host=localhost;Database=auditdb")
                .Options
        );
}
