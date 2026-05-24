using System.Diagnostics;
using Medallion.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ratatoskr.Core;

namespace Ratatoskr.EfCore.Internal;

internal partial class InboxCleanupService<TDbContext>(
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
        LogStartingInboxCleanupService(logger);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(_options.CleanupInterval, timeProvider, stoppingToken);

            try
            {
                await TryCleanupWithLockAsync(stoppingToken);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                LogInboxCleanupServiceError(logger, e);
            }
        }

        LogStoppedInboxCleanupService(logger);
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
            LogInboxCleanupServiceSkippedLock(logger);
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
            LogInboxCleanupServiceDeleted(logger, totalStatusesDeleted, totalMessagesDeleted);
        }

        return (totalStatusesDeleted, totalMessagesDeleted);
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Starting InboxCleanupService"
    )]
    private static partial void LogStartingInboxCleanupService(ILogger logger);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Error,
        Message = "InboxCleanupService encountered an error during cleanup"
    )]
    private static partial void LogInboxCleanupServiceError(ILogger logger, Exception ex);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Information,
        Message = "Stopped InboxCleanupService"
    )]
    private static partial void LogStoppedInboxCleanupService(ILogger logger);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Debug,
        Message = "InboxCleanupService skipped — another instance holds the lock"
    )]
    private static partial void LogInboxCleanupServiceSkippedLock(ILogger logger);

    [LoggerMessage(
        EventId = 5,
        Level = LogLevel.Information,
        Message = "InboxCleanupService deleted {StatusCount} handler status(es) and {MessageCount} orphaned message(s)"
    )]
    private static partial void LogInboxCleanupServiceDeleted(
        ILogger logger,
        int statusCount,
        int messageCount
    );
}
