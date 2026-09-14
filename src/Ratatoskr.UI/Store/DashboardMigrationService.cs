using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ratatoskr.UI.Store;

/// <summary>
/// Applies the dashboard's migrations at startup when the host opted in.
/// </summary>
/// <remarks>
/// Runs before <see cref="DashboardServiceStore"/> so the snapshot table exists by the time it is
/// read. When <see cref="RatatoskrDashboardStoreOptions.AutoMigrate"/> is off this only checks
/// that the schema is present, and says so plainly if it is not — an empty dashboard with a
/// database error buried in the logs is a worse first experience than a clear startup failure.
/// </remarks>
internal sealed partial class DashboardMigrationService(
    IServiceScopeFactory scopeFactory,
    IOptions<RatatoskrDashboardStoreOptions> options,
    ILogger<DashboardMigrationService> logger
) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RatatoskrDashboardDbContext>();

        if (options.Value.AutoMigrate)
        {
            await db.Database.MigrateAsync(cancellationToken);
            LogMigrated(logger);
            return;
        }

        var pending = await db.Database.GetPendingMigrationsAsync(cancellationToken);
        var names = pending.ToArray();
        if (names.Length > 0)
        {
            throw new InvalidOperationException(
                $"The Ratatoskr dashboard database is missing {names.Length} migration(s): {string.Join(", ", names)}. "
                    + "Apply them from your deployment step, or set AutoMigrate for a single-node setup. "
                    + "Do not use EnsureCreated: it creates nothing when the database already exists."
            );
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Applied pending Ratatoskr dashboard migrations."
    )]
    private static partial void LogMigrated(ILogger logger);
}
