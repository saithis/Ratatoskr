using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Ratatoskr.UI.Store;

/// <summary>
/// Used only by <c>dotnet ef</c> to build the model when generating migrations. SQLite is chosen
/// deliberately: it is the least capable provider we support, so a migration that it can express
/// contains nothing provider-specific and applies just as well on PostgreSQL.
/// </summary>
internal sealed class RatatoskrDashboardDbContextFactory
    : IDesignTimeDbContextFactory<RatatoskrDashboardDbContext>
{
    public RatatoskrDashboardDbContext CreateDbContext(string[] args) =>
        new(
            new DbContextOptionsBuilder<RatatoskrDashboardDbContext>()
                .UseSqlite("Data Source=ratatoskr-dashboard-design.db")
                .Options
        );
}
