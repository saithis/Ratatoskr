using Medallion.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ratatoskr.EfCore.Internal;

/// <summary>
/// Background service that periodically deletes old processed and poisoned outbox messages.
/// Processed messages are deleted after <see cref="OutboxOptions.CompletedRetention"/>.
/// Poisoned messages are deleted after <see cref="OutboxOptions.PoisonedRetention"/>.
/// </summary>
internal class OutboxCleanupProcessor<TDbContext>(
    IServiceScopeFactory serviceScopeFactory,
    IDistributedLockProvider distributedLockProvider,
    TimeProvider timeProvider,
    OutboxOptionsRegistry optionsRegistry,
    OutboxTelemetry telemetry,
    ILogger<OutboxCleanupProcessor<TDbContext>> logger)
    : PollingBackgroundService(distributedLockProvider, timeProvider, logger)
    where TDbContext : DbContext, IOutboxDbContext
{
    private readonly OutboxOptions _options = optionsRegistry.Get(typeof(TDbContext));

    protected override string ProcessorName => $"OutboxCleanupProcessor<{typeof(TDbContext).Name}>";
    protected override TimeSpan PollingInterval => _options.CleanupInterval;
    protected override TimeSpan RestartDelay => _options.RestartDelay;
    protected override TimeSpan LockAcquireTimeout => _options.LockAcquireTimeout;
    protected override string LockName => $"OutboxCleanup-{typeof(TDbContext).Name}";

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
            var deleted = await dbContext.Set<OutboxMessageEntity>()
                .Where(m => m.ProcessedAt != null && m.ProcessedAt < cutoff)
                .ExecuteDeleteAsync(cancellationToken);

            if (deleted > 0)
            {
                logger.LogInformation("Outbox cleanup: deleted {Count} processed messages older than {Retention}",
                    deleted, completedRetention);
                telemetry.RecordCleanup(deleted, "completed");
            }
        }

        // Delete poisoned messages
        if (_options.PoisonedRetention is { } poisonedRetention)
        {
            var cutoff = now - poisonedRetention;
            var deleted = await dbContext.Set<OutboxMessageEntity>()
                .Where(m => m.IsPoisoned && m.CreatedAt < cutoff)
                .ExecuteDeleteAsync(cancellationToken);

            if (deleted > 0)
            {
                logger.LogInformation("Outbox cleanup: deleted {Count} poisoned messages older than {Retention}",
                    deleted, poisonedRetention);
                telemetry.RecordCleanup(deleted, "poisoned");
            }
        }
    }
}
