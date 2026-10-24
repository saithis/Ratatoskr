using System.Diagnostics;
using Medallion.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ratatoskr.Core;

namespace Ratatoskr.EfCore.Internal;

/// <summary>
/// Base class for background services that periodically delete rows older than the configured
/// <see cref="DurableProcessingOptions.RetentionPeriod"/>. Only one instance runs a cleanup cycle
/// at a time, guarded by a distributed lock; other instances skip the cycle.
/// </summary>
internal abstract partial class RetentionCleanupService<TDbContext>(
    IServiceScopeFactory serviceScopeFactory,
    DurableProcessingOptions options,
    IDistributedLockProvider distributedLockProvider,
    TimeProvider timeProvider,
    ILogger logger
) : BackgroundService
    where TDbContext : DbContext, IRatatoskrDbContext
{
    protected abstract string ServiceName { get; }

    protected ILogger Logger => logger;

    /// <summary>
    /// Deletes all rows that have exceeded the retention period.
    /// </summary>
    protected abstract Task RunCleanupAsync(CancellationToken cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStartingCleanupService(logger, ServiceName);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(options.CleanupInterval, timeProvider, stoppingToken);

            try
            {
                await TryCleanupWithLockAsync(stoppingToken);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                LogCleanupServiceError(logger, e, ServiceName);
            }
        }

        LogStoppedCleanupService(logger, ServiceName);
    }

    internal async Task<bool> TryCleanupWithLockAsync(CancellationToken cancellationToken)
    {
        await using var dLock = await distributedLockProvider.TryAcquireLockAsync(
            options.CleanupLockName,
            TimeSpan.Zero,
            cancellationToken
        );

        if (dLock == null)
        {
            LogCleanupServiceSkippedLock(logger, ServiceName);
            RatatoskrDiagnostics.LockAcquisitionFailure.Add(
                1,
                new TagList { { "processor", ServiceName } }
            );
            return false;
        }

        await RunCleanupAsync(cancellationToken);
        return true;
    }

    /// <summary>The point in time before which processed rows are deleted.</summary>
    protected DateTimeOffset GetCutoff() => timeProvider.GetUtcNow() - options.RetentionPeriod!.Value;

    /// <summary>
    /// Repeatedly runs <paramref name="deleteBatch"/> (each time with a fresh scope and DbContext)
    /// until a batch deletes fewer than <see cref="DurableProcessingOptions.CleanupBatchSize"/> rows.
    /// <paramref name="deleteBatch"/> receives the batch size and must delete at most that many rows.
    /// Returns the total number of deleted rows.
    /// </summary>
    protected async Task<int> DeleteInBatchesAsync(
        Func<TDbContext, int, CancellationToken, Task<int>> deleteBatch,
        Action<int> recordDeleted,
        CancellationToken cancellationToken
    )
    {
        var totalDeleted = 0;
        int deleted;
        do
        {
            using var scope = serviceScopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();

            deleted = await deleteBatch(dbContext, options.CleanupBatchSize, cancellationToken);
            if (deleted > 0)
            {
                recordDeleted(deleted);
            }

            totalDeleted += deleted;
        } while (deleted == options.CleanupBatchSize);

        return totalDeleted;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Starting {Service}")]
    private static partial void LogStartingCleanupService(ILogger logger, string service);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Error,
        Message = "{Service} encountered an error during cleanup"
    )]
    private static partial void LogCleanupServiceError(
        ILogger logger,
        Exception ex,
        string service
    );

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Stopped {Service}")]
    private static partial void LogStoppedCleanupService(ILogger logger, string service);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Debug,
        Message = "{Service} skipped, another instance holds the lock"
    )]
    private static partial void LogCleanupServiceSkippedLock(ILogger logger, string service);
}
