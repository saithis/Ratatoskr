using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace InventoryService.Infrastructure;

internal static class DatabaseMigrationHelper
{
    public static async Task MigrateAsync(DbContext db, CancellationToken cancellationToken = default)
    {
        if (await db.Database.CanConnectAsync(cancellationToken))
        {
            var creator = db.Database.GetService<IRelationalDatabaseCreator>();
            if (await creator.HasTablesAsync(cancellationToken))
            {
                var hasHistory = false;
                await using (var conn = db.Database.GetDbConnection())
                {
                    await conn.OpenAsync(cancellationToken);
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT 1 FROM information_schema.tables WHERE table_name = '__EFMigrationsHistory'";
                    var result = await cmd.ExecuteScalarAsync(cancellationToken);
                    hasHistory = result != null;
                }

                if (!hasHistory)
                {
                    // Database was created with EnsureCreated before migrations were introduced.
                    // Reset the development database so migrations apply cleanly with history.
                    await db.Database.EnsureDeletedAsync(cancellationToken);
                }
            }
        }

        await db.Database.MigrateAsync(cancellationToken);
    }
}
