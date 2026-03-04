using Medallion.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ratatoskr.EfCore.Internal;

internal class OutboxProcessor<TDbContext>(
    IServiceScopeFactory serviceScopeFactory,
    IDistributedLockProvider distributedLockProvider,
    TimeProvider timeProvider,
    OutboxOptionsRegistry optionsRegistry,
    ILogger<OutboxProcessor<TDbContext>> logger)
    : PollingBackgroundService(distributedLockProvider, timeProvider, logger)
    where TDbContext : DbContext, IOutboxDbContext
{
    private readonly OutboxOptions _options = optionsRegistry.Get(typeof(TDbContext));

    protected override string ProcessorName => $"OutboxProcessor<{typeof(TDbContext).Name}>";
    protected override TimeSpan PollingInterval => _options.PollingInterval;
    protected override TimeSpan RestartDelay => _options.RestartDelay;
    protected override TimeSpan LockAcquireTimeout => _options.LockAcquireTimeout;
    protected override string LockName => _options.LockName;

    protected override async Task ProcessBatchesAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            // Create a fresh scope (and DbContext) per batch to avoid stale EF state
            using var batchScope = serviceScopeFactory.CreateScope();
            var processor = batchScope.ServiceProvider.GetRequiredService<OutboxMessageProcessor<TDbContext>>();

            logger.LogDebug("Checking outbox for unsent messages");
            var processedCount = await processor.ProcessBatchAsync(
                includeStuckMessageDetection: true,
                cancellationToken);

            if (processedCount == 0)
                return;
        }
    }
}
