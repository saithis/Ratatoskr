using System.Diagnostics;
using Medallion.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ratatoskr.Core;

namespace Ratatoskr.EfCore.Internal;

internal partial class InboxCleanupService<TDbContext>(
    IServiceScopeFactory serviceScopeFactory,
    InboxOptionsHolder<TDbContext> optionsHolder,
    IDistributedLockProvider distributedLockProvider,
    TimeProvider timeProvider,
    ILogger<InboxCleanupService<TDbContext>> logger
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
    protected override string ServiceName => "InboxCleanupService";

    protected override Task RunCleanupAsync(CancellationToken cancellationToken) =>
        CleanupAsync(cancellationToken);

    internal async Task<(int HandlerStatuses, int OrphanedMessages)> CleanupAsync(
        CancellationToken cancellationToken
    )
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var cutoff = GetCutoff();

        // Step 1: Delete completed (non-poisoned) handler statuses older than retention period
        var totalStatusesDeleted = await DeleteInBatchesAsync(
            (dbContext, batchSize, ct) =>
                dbContext
                    .Set<InboxHandlerStatusEntity>()
                    .Where(x => x.CompletedAt != null && !x.IsPoisoned && x.CompletedAt < cutoff)
                    .OrderBy(x => x.CompletedAt)
                    .Take(batchSize)
                    .ExecuteDeleteAsync(ct),
            deleted => RatatoskrDiagnostics.InboxCleanupStatusCount.Add(deleted),
            cancellationToken
        );

        // Step 2: Delete orphaned inbox messages (no remaining handler statuses)
        var totalMessagesDeleted = await DeleteInBatchesAsync(
            (dbContext, batchSize, ct) =>
                dbContext
                    .Set<InboxMessageEntity>()
                    .Where(m =>
                        !dbContext.Set<InboxHandlerStatusEntity>().Any(s => s.MessageId == m.Id)
                    )
                    .OrderBy(m => m.Id)
                    .Take(batchSize)
                    .ExecuteDeleteAsync(ct),
            deleted => RatatoskrDiagnostics.InboxCleanupMessageCount.Add(deleted),
            cancellationToken
        );

        RatatoskrDiagnostics.InboxCleanupDuration.Record(
            Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds
        );

        if (totalStatusesDeleted > 0 || totalMessagesDeleted > 0)
        {
            LogInboxCleanupServiceDeleted(Logger, totalStatusesDeleted, totalMessagesDeleted);
        }

        return (totalStatusesDeleted, totalMessagesDeleted);
    }

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
