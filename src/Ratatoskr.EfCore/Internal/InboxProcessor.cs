using Medallion.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ratatoskr.EfCore.Internal;

internal class InboxProcessor<TDbContext>(
    IServiceScopeFactory serviceScopeFactory,
    IDistributedLockProvider distributedLockProvider,
    TimeProvider timeProvider,
    InboxOptionsHolder<TDbContext> optionsHolder,
    ILogger<InboxProcessor<TDbContext>> logger)
    : PollingBackgroundService(distributedLockProvider, timeProvider, logger), IProcessorTrigger
    where TDbContext : DbContext, IInboxDbContext
{
    private readonly InboxOptions _options = optionsHolder.Options;

    protected override string ProcessorName => "InboxProcessor";
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
            var processor = batchScope.ServiceProvider.GetRequiredService<InboxMessageProcessor<TDbContext>>();

            logger.LogDebug("Checking inbox for pending handler deliveries");
            var processed = await processor.ProcessBatchAsync(
                includeStuckMessageDetection: true,
                cancellationToken);

            if (processed == 0)
                return;
        }
    }
}
