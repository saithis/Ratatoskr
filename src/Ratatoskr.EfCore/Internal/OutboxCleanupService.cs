using System.Diagnostics;
using Medallion.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ratatoskr.Core;

namespace Ratatoskr.EfCore.Internal;

internal partial class OutboxCleanupService<TDbContext>(
    IServiceScopeFactory serviceScopeFactory,
    OutboxOptionsHolder<TDbContext> optionsHolder,
    IDistributedLockProvider distributedLockProvider,
    TimeProvider timeProvider,
    ILogger<OutboxCleanupService<TDbContext>> logger
) : BackgroundService
    where TDbContext : DbContext, IOutboxDbContext
{
    private readonly OutboxOptions _options = optionsHolder.Options;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStartingOutboxCleanupService(logger);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(_options.CleanupInterval, timeProvider, stoppingToken);

            try
            {
                await TryCleanupWithLockAsync(stoppingToken);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                LogOutboxCleanupServiceError(logger, e);
            }
        }

        LogStoppedOutboxCleanupService(logger);
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
            LogOutboxCleanupServiceSkippedLock(logger);
            RatatoskrDiagnostics.LockAcquisitionFailure.Add(
                1,
                new TagList { { "processor", "OutboxCleanupService" } }
            );
            return false;
        }

        await CleanupAsync(cancellationToken);
        return true;
    }

    internal async Task<int> CleanupAsync(CancellationToken cancellationToken)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var cutoff = timeProvider.GetUtcNow() - _options.RetentionPeriod!.Value;
        var totalDeleted = 0;
        int deleted;

        do
        {
            using var scope = serviceScopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();

            deleted = await dbContext
                .Set<OutboxMessageEntity>()
                .Where(x => x.ProcessedAt != null && !x.IsPoisoned && x.ProcessedAt < cutoff)
                .OrderBy(x => x.ProcessedAt)
                .Take(_options.CleanupBatchSize)
                .ExecuteDeleteAsync(cancellationToken);

            if (deleted > 0)
            {
                RatatoskrDiagnostics.OutboxCleanupCount.Add(deleted);
            }

            totalDeleted += deleted;
        } while (deleted == _options.CleanupBatchSize);

        RatatoskrDiagnostics.OutboxCleanupDuration.Record(
            Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds
        );
        if (totalDeleted > 0)
        {
            LogOutboxCleanupServiceDeleted(logger, totalDeleted);
        }

        return totalDeleted;
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Starting OutboxCleanupService"
    )]
    private static partial void LogStartingOutboxCleanupService(ILogger logger);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Error,
        Message = "OutboxCleanupService encountered an error during cleanup"
    )]
    private static partial void LogOutboxCleanupServiceError(ILogger logger, Exception ex);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Information,
        Message = "Stopped OutboxCleanupService"
    )]
    private static partial void LogStoppedOutboxCleanupService(ILogger logger);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Debug,
        Message = "OutboxCleanupService skipped — another instance holds the lock"
    )]
    private static partial void LogOutboxCleanupServiceSkippedLock(ILogger logger);

    [LoggerMessage(
        EventId = 5,
        Level = LogLevel.Information,
        Message = "OutboxCleanupService deleted {Count} processed message(s)"
    )]
    private static partial void LogOutboxCleanupServiceDeleted(ILogger logger, int count);
}
