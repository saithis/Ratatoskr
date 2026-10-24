using System.Diagnostics;
using Medallion.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ratatoskr.Core;

namespace Ratatoskr.EfCore.Internal;

internal partial class OutboxCleanupService<TDbContext>(
    IServiceScopeFactory serviceScopeFactory,
    OutboxOptionsHolder<TDbContext> optionsHolder,
    IDistributedLockProvider distributedLockProvider,
    TimeProvider timeProvider,
    ILogger<OutboxCleanupService<TDbContext>> logger
)
    : RetentionCleanupService<TDbContext>(
        serviceScopeFactory,
        optionsHolder.Options,
        distributedLockProvider,
        timeProvider,
        logger
    )
    where TDbContext : DbContext, IRatatoskrDbContext
{
    protected override string ServiceName => "OutboxCleanupService";

    protected override Task RunCleanupAsync(CancellationToken cancellationToken) =>
        CleanupAsync(cancellationToken);

    internal async Task<int> CleanupAsync(CancellationToken cancellationToken)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var cutoff = GetCutoff();

        var totalDeleted = await DeleteInBatchesAsync(
            (dbContext, batchSize, ct) =>
                dbContext
                    .Set<OutboxMessageEntity>()
                    .Where(x => x.ProcessedAt != null && !x.IsPoisoned && x.ProcessedAt < cutoff)
                    .OrderBy(x => x.ProcessedAt)
                    .Take(batchSize)
                    .ExecuteDeleteAsync(ct),
            deleted => RatatoskrDiagnostics.OutboxCleanupCount.Add(deleted),
            cancellationToken
        );

        RatatoskrDiagnostics.OutboxCleanupDuration.Record(
            Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds
        );
        if (totalDeleted > 0)
        {
            LogOutboxCleanupServiceDeleted(Logger, totalDeleted);
        }

        return totalDeleted;
    }

    [LoggerMessage(
        EventId = 5,
        Level = LogLevel.Information,
        Message = "OutboxCleanupService deleted {Count} processed message(s)"
    )]
    private static partial void LogOutboxCleanupServiceDeleted(ILogger logger, int count);
}
