using Medallion.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ratatoskr.EfCore;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Management.Agent;

namespace Ratatoskr.Management.EfCore.Idempotency;

/// <summary>
/// Prunes completed operation records once they are older than the configured retention.
/// </summary>
/// <remarks>
/// Deliberately its own worker rather than a job bolted onto the inbox cleanup service. That one
/// only runs when an inbox is configured <em>and</em> a retention period is set, so an outbox-only
/// service — a perfectly ordinary shape — would accumulate operation records forever. This runs
/// whenever the EF Core management package is installed, which is exactly when the table exists.
/// <para>
/// In-progress records are never pruned by age: one of those means a run was interrupted, and
/// deleting it would turn a resumable operation into a silently restarted one.
/// </para>
/// </remarks>
internal sealed partial class ManagementOperationCleanupService<TDbContext>(
    IServiceScopeFactory scopeFactory,
    IOptions<ManagementAgentOptions> options,
    IDistributedLockProvider locks,
    TimeProvider timeProvider,
    ILogger<ManagementOperationCleanupService<TDbContext>> logger
) : BackgroundService
    where TDbContext : DbContext, IOutboxDbContext, IInboxDbContext
{
    private const int BatchSize = 500;

    internal static string LockName { get; } = $"ManagementOperationCleanup_{typeof(TDbContext).Name}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(options.Value.OperationLogCleanupInterval, timeProvider, stoppingToken);

            try
            {
                await TryCleanupWithLockAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                LogCleanupFailed(logger, typeof(TDbContext).Name, ex);
            }
        }
    }

    /// <summary>Runs one pass if this replica wins the lock. Returns whether it ran.</summary>
    internal async Task<bool> TryCleanupWithLockAsync(CancellationToken cancellationToken)
    {
        // Every replica runs this worker, but the table is shared. Without the lock they would all
        // delete the same rows at the same time and deadlock each other for no benefit.
        await using var handle = await locks.TryAcquireLockAsync(
            LockName,
            TimeSpan.Zero,
            cancellationToken
        );

        if (handle is null)
        {
            return false;
        }

        await CleanupAsync(cancellationToken);
        return true;
    }

    /// <summary>Deletes expired records in bounded batches. Returns how many were removed.</summary>
    internal async Task<int> CleanupAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TDbContext>();

        var cutoff = timeProvider.GetUtcNow() - options.Value.OperationLogRetention;
        var removed = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var batch = await db.Set<ManagementOperationEntity>()
                .Where(x =>
                    x.State == ManagementOperationState.Completed
                    && x.CompletedAt != null
                    && x.CompletedAt < cutoff
                )
                .OrderBy(x => x.CompletedAt)
                .Take(BatchSize)
                .ExecuteDeleteAsync(cancellationToken);

            removed += batch;
            if (batch < BatchSize)
            {
                break;
            }
        }

        if (removed > 0)
        {
            LogPruned(logger, removed, typeof(TDbContext).Name);
        }

        return removed;
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Debug,
        Message = "Pruned {Count} completed management operation records from {DbContext}."
    )]
    private static partial void LogPruned(ILogger logger, int count, string dbContext);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "Management operation log cleanup failed for {DbContext}."
    )]
    private static partial void LogCleanupFailed(
        ILogger logger,
        string dbContext,
        Exception exception
    );
}
