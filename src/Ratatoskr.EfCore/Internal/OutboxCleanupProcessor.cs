using Medallion.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ratatoskr.EfCore.Internal;

/// <summary>
/// Background service that periodically deletes old processed and poisoned outbox messages.
/// Processed messages are deleted after <see cref="OutboxOptions.CompletedRetention"/>.
/// Poisoned messages are deleted after <see cref="OutboxOptions.PoisonedRetention"/>.
/// Cleanup is scoped to this DbContext via the <see cref="OutboxMessageEntity.SourceContext"/>
/// discriminator to prevent cross-contamination when multiple DbContexts share the same physical database.
/// Legacy rows (empty SourceContext) are cleaned up by any DbContext.
/// </summary>
internal class OutboxCleanupProcessor<TDbContext>(
    IServiceScopeFactory serviceScopeFactory,
    IDistributedLockProvider distributedLockProvider,
    TimeProvider timeProvider,
    TypedOptionsRegistry<OutboxOptions> optionsRegistry,
    OutboxTelemetry telemetry,
    ILogger<OutboxCleanupProcessor<TDbContext>> logger)
    : PollingBackgroundService(distributedLockProvider, timeProvider, logger)
    where TDbContext : DbContext, IOutboxDbContext
{
    private readonly OutboxOptions _options = optionsRegistry.Get(typeof(TDbContext));
    private readonly string _sourceContext = typeof(TDbContext).FullName!;

    protected override string ProcessorName => $"OutboxCleanupProcessor<{typeof(TDbContext).Name}>";
    protected override TimeSpan PollingInterval => _options.CleanupInterval;
    protected override TimeSpan RestartDelay => _options.RestartDelay;
    protected override TimeSpan LockAcquireTimeout => _options.LockAcquireTimeout;
    protected override string LockName => $"OutboxCleanup-{typeof(TDbContext).FullName}";

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

        // Delete successfully processed messages
        if (_options.CompletedRetention is { } completedRetention)
        {
            var cutoff = now - completedRetention;
            var totalDeleted = 0;

            while (true)
            {
                var deleted = await dbContext.Set<OutboxMessageEntity>()
                    .Where(m => m.SourceContext == _sourceContext || m.SourceContext == "")
                    .Where(m => m.ProcessedAt != null && m.ProcessedAt < cutoff)
                    .Take(_options.CleanupBatchSize)
                    .ExecuteDeleteAsync(cancellationToken);

                totalDeleted += deleted;
                if (deleted < _options.CleanupBatchSize)
                    break;
            }

            if (totalDeleted > 0)
            {
                logger.LogInformation("Outbox cleanup: deleted {Count} processed messages older than {Retention}",
                    totalDeleted, completedRetention);
                telemetry.RecordCleanup(totalDeleted, "completed");
            }
        }

        // Delete poisoned messages
        if (_options.PoisonedRetention is { } poisonedRetention)
        {
            var cutoff = now - poisonedRetention;
            var totalDeleted = 0;

            while (true)
            {
                var deleted = await dbContext.Set<OutboxMessageEntity>()
                    .Where(m => m.SourceContext == _sourceContext || m.SourceContext == "")
                    .Where(m => m.IsPoisoned && m.CreatedAt < cutoff)
                    .Take(_options.CleanupBatchSize)
                    .ExecuteDeleteAsync(cancellationToken);

                totalDeleted += deleted;
                if (deleted < _options.CleanupBatchSize)
                    break;
            }

            if (totalDeleted > 0)
            {
                logger.LogInformation("Outbox cleanup: deleted {Count} poisoned messages older than {Retention}",
                    totalDeleted, poisonedRetention);
                telemetry.RecordCleanup(totalDeleted, "poisoned");
            }
        }
    }
}
