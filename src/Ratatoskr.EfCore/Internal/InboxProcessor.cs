using Medallion.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ratatoskr.Core;

namespace Ratatoskr.EfCore.Internal;

internal class InboxProcessor<TDbContext>(
    IServiceScopeFactory serviceScopeFactory,
    IDistributedLockProvider distributedLockProvider,
    InboxTelemetry telemetry,
    TimeProvider timeProvider,
    IOptions<InboxOptions> options,
    ILogger<InboxProcessor<TDbContext>> logger)
    : PollingBackgroundService(distributedLockProvider, timeProvider, logger)
    where TDbContext : DbContext, IInboxDbContext
{
    private readonly InboxOptions _options = options.Value;

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
            var sp = batchScope.ServiceProvider;

            var dbContext = sp.GetRequiredService<TDbContext>();
            var handlerRegistry = sp.GetRequiredService<InboxHandlerRegistry>();
            var observers = sp.GetServices<IMessageActivityObserver>();
            var messageSerializer = sp.GetRequiredService<IMessageSerializer>();

            var handlerInvoker = sp.GetRequiredService<HandlerInvoker>();

            var processor = new InboxMessageProcessor<TDbContext>(
                dbContext, handlerInvoker, handlerRegistry, telemetry, timeProvider,
                _options, observers, messageSerializer, logger);

            logger.LogDebug("Checking inbox for pending handler deliveries");
            var processed = await processor.ProcessBatchAsync(
                includeStuckMessageDetection: true,
                cancellationToken);

            if (processed == 0)
                return;
        }
    }
}
