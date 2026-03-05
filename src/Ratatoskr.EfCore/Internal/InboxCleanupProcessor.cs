using Medallion.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ratatoskr.EfCore.Internal;

/// <summary>
/// Background service that periodically deletes old completed and poisoned inbox messages.
/// Completed messages (all handlers succeeded) are deleted after <see cref="InboxOptions.CompletedRetention"/>.
/// Poisoned messages (at least one handler poisoned, all terminal) are deleted after <see cref="InboxOptions.PoisonedRetention"/>.
/// Cascade delete on the foreign key handles handler status rows automatically.
/// Cleanup is scoped to channels belonging to this DbContext to prevent cross-contamination
/// when multiple DbContexts share the same physical database.
/// </summary>
internal class InboxCleanupProcessor<TDbContext>(
    IServiceScopeFactory serviceScopeFactory,
    IDistributedLockProvider distributedLockProvider,
    TimeProvider timeProvider,
    InboxDbContextRegistry dbContextRegistry,
    InboxRoutingTable routingTable,
    InboxTelemetry telemetry,
    ILogger<InboxCleanupProcessor<TDbContext>> logger)
    : PollingBackgroundService(distributedLockProvider, timeProvider, logger)
    where TDbContext : DbContext, IInboxDbContext
{
    private readonly InboxOptions _options = dbContextRegistry.GetOptions(typeof(TDbContext));
    private readonly IReadOnlyList<string> _channelNames = routingTable.GetChannelNamesForDbContext(typeof(TDbContext));

    protected override string ProcessorName => $"InboxCleanupProcessor<{typeof(TDbContext).Name}>";
    protected override TimeSpan PollingInterval => _options.CleanupInterval;
    protected override TimeSpan RestartDelay => _options.RestartDelay;
    protected override TimeSpan LockAcquireTimeout => _options.LockAcquireTimeout;
    protected override string LockName => $"InboxCleanup-{typeof(TDbContext).FullName}";

    /// <summary>
    /// Runs one cleanup pass without requiring the background service infrastructure.
    /// Used by integration tests.
    /// </summary>
    internal Task RunOnceAsync(CancellationToken cancellationToken) =>
        ProcessBatchesAsync(cancellationToken);

    protected override async Task ProcessBatchesAsync(CancellationToken cancellationToken)
    {
        using var scope = serviceScopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();
        var now = timeProvider.GetUtcNow();

        // Delete fully completed messages (all handlers completed, none poisoned)
        if (_options.CompletedRetention is { } completedRetention)
        {
            var cutoff = now - completedRetention;
            var totalDeleted = 0;

            while (true)
            {
                var deleted = await dbContext.Set<InboxMessageEntity>()
                    .Where(m => _channelNames.Contains(m.ChannelName))
                    .Where(m => m.ReceivedAt < cutoff
                        // At least one handler status must exist (orphaned messages without handlers are not "completed")
                        && dbContext.Set<InboxHandlerStatusEntity>()
                            .Any(s => s.MessageId == m.Id)
                        // No pending handlers (not completed and not poisoned)
                        && !dbContext.Set<InboxHandlerStatusEntity>()
                            .Any(s => s.MessageId == m.Id && s.CompletedAt == null && !s.IsPoisoned)
                        // No poisoned handlers
                        && !dbContext.Set<InboxHandlerStatusEntity>()
                            .Any(s => s.MessageId == m.Id && s.IsPoisoned))
                    .Take(_options.CleanupBatchSize)
                    .ExecuteDeleteAsync(cancellationToken);

                totalDeleted += deleted;
                if (deleted < _options.CleanupBatchSize)
                    break;
            }

            if (totalDeleted > 0)
            {
                logger.LogInformation("Inbox cleanup: deleted {Count} completed messages older than {Retention}",
                    totalDeleted, completedRetention);
                telemetry.RecordCleanup(totalDeleted, "completed");
            }
        }

        // Delete poisoned messages (all handlers terminal, at least one poisoned)
        if (_options.PoisonedRetention is { } poisonedRetention)
        {
            var cutoff = now - poisonedRetention;
            var totalDeleted = 0;

            while (true)
            {
                var deleted = await dbContext.Set<InboxMessageEntity>()
                    .Where(m => _channelNames.Contains(m.ChannelName))
                    .Where(m => m.ReceivedAt < cutoff
                        // No pending handlers
                        && !dbContext.Set<InboxHandlerStatusEntity>()
                            .Any(s => s.MessageId == m.Id && s.CompletedAt == null && !s.IsPoisoned)
                        // At least one poisoned handler
                        && dbContext.Set<InboxHandlerStatusEntity>()
                            .Any(s => s.MessageId == m.Id && s.IsPoisoned))
                    .Take(_options.CleanupBatchSize)
                    .ExecuteDeleteAsync(cancellationToken);

                totalDeleted += deleted;
                if (deleted < _options.CleanupBatchSize)
                    break;
            }

            if (totalDeleted > 0)
            {
                logger.LogInformation("Inbox cleanup: deleted {Count} poisoned messages older than {Retention}",
                    totalDeleted, poisonedRetention);
                telemetry.RecordCleanup(totalDeleted, "poisoned");
            }
        }
    }
}
