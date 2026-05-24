using System.Diagnostics;
using Medallion.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ratatoskr.Core;

namespace Ratatoskr.EfCore.Internal;

internal class InboxCleanupService<TDbContext>(
    IServiceScopeFactory serviceScopeFactory,
    InboxOptionsHolder<TDbContext> optionsHolder,
    IDistributedLockProvider distributedLockProvider,
    TimeProvider timeProvider,
    ILogger<InboxCleanupService<TDbContext>> logger
) : BackgroundService
    where TDbContext : DbContext, IInboxDbContext
{
    private readonly InboxOptions _options = optionsHolder.Options;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Starting InboxCleanupService");

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(_options.CleanupInterval, timeProvider, stoppingToken);

            try
            {
                await TryCleanupWithLockAsync(stoppingToken);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(e, "InboxCleanupService encountered an error during cleanup");
            }
        }

        logger.LogInformation("Stopped InboxCleanupService");
    }

    internal async Task<bool> TryCleanupWithLockAsync(CancellationToken cancellationToken)
    {
        await using var dLock = await distributedLockProvider.TryAcquireLockAsync(
            _options.CleanupLockName,
            TimeSpan.Zero,
            cancellationToken
        );

        if (dLock == null)
        {
            logger.LogDebug("InboxCleanupService skipped — another instance holds the lock");
            RatatoskrDiagnostics.LockAcquisitionFailure.Add(
                1,
                new TagList { { "processor", "InboxCleanupService" } }
            );
            return false;
        }

        await CleanupAsync(cancellationToken);
        return true;
    }

    internal async Task<(int HandlerStatuses, int OrphanedMessages)> CleanupAsync(
        CancellationToken cancellationToken
    )
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var cutoff = timeProvider.GetUtcNow() - _options.RetentionPeriod!.Value;
        var totalStatusesDeleted = 0;
        int deleted;

        // Step 1: Delete completed (non-poisoned) handler statuses older than retention period
        do
        {
            using var scope = serviceScopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();

            deleted = await dbContext
                .Set<InboxHandlerStatusEntity>()
                .Where(x => x.CompletedAt != null && !x.IsPoisoned && x.CompletedAt < cutoff)
                .OrderBy(x => x.CompletedAt)
                .Take(_options.CleanupBatchSize)
                .ExecuteDeleteAsync(cancellationToken);

            if (deleted > 0)
            {
                RatatoskrDiagnostics.InboxCleanupStatusCount.Add(deleted);
            }

            totalStatusesDeleted += deleted;
        } while (deleted == _options.CleanupBatchSize);

        // Step 2: Delete orphaned inbox messages (no remaining handler statuses)
        var totalMessagesDeleted = 0;
        do
        {
            using var scope = serviceScopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();

            deleted = await dbContext
                .Set<InboxMessageEntity>()
                .Where(m =>
                    !dbContext.Set<InboxHandlerStatusEntity>().Any(s => s.MessageId == m.Id)
                )
                .OrderBy(m => m.Id)
                .Take(_options.CleanupBatchSize)
                .ExecuteDeleteAsync(cancellationToken);

            if (deleted > 0)
            {
                RatatoskrDiagnostics.InboxCleanupMessageCount.Add(deleted);
            }

            totalMessagesDeleted += deleted;
        } while (deleted == _options.CleanupBatchSize);

        RatatoskrDiagnostics.InboxCleanupDuration.Record(
            Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds
        );

        if (totalStatusesDeleted > 0 || totalMessagesDeleted > 0)
        {
            logger.LogInformation(
                "InboxCleanupService deleted {StatusCount} handler status(es) and {MessageCount} orphaned message(s)",
                totalStatusesDeleted,
                totalMessagesDeleted
            );
        }

        return (totalStatusesDeleted, totalMessagesDeleted);
    }
}
