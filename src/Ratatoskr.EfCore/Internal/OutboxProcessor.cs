using Medallion.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ratatoskr.Core;

namespace Ratatoskr.EfCore.Internal;

internal class OutboxProcessor<TDbContext>(
    IServiceScopeFactory serviceScopeFactory,
    IDistributedLockProvider distributedLockProvider,
    TimeProvider timeProvider,
    IOptions<OutboxOptions> options,
    ILogger<OutboxProcessor<TDbContext>> logger)
    : PollingBackgroundService(distributedLockProvider, timeProvider, logger)
    where TDbContext : DbContext, IOutboxDbContext
{
    private readonly OutboxOptions _options = options.Value;

    protected override string ProcessorName => "OutboxProcessor";
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

            var dbContext = batchScope.ServiceProvider.GetRequiredService<TDbContext>();
            var messageSenders = batchScope.ServiceProvider.GetServices<IMessageSender>();
            var activityObservers = batchScope.ServiceProvider.GetServices<IMessageActivityObserver>();

            var processor = new OutboxMessageProcessor<TDbContext>(
                dbContext, messageSenders, timeProvider, _options, activityObservers, logger);

            logger.LogDebug("Checking outbox for unsent messages");
            var processedCount = await processor.ProcessBatchAsync(
                includeStuckMessageDetection: true,
                cancellationToken);

            if (processedCount == 0)
                return;
        }
    }
}
