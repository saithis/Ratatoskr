using Medallion.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ratatoskr.EfCore.Internal;

internal partial class OutboxProcessor<TDbContext>(
    IServiceScopeFactory serviceScopeFactory,
    IDistributedLockProvider distributedLockProvider,
    TimeProvider timeProvider,
    OutboxOptionsHolder<TDbContext> optionsHolder,
    ILogger<OutboxProcessor<TDbContext>> logger
) : PollingBackgroundService(distributedLockProvider, timeProvider, logger)
    where TDbContext : DbContext, IOutboxDbContext
{
    private readonly OutboxOptions _options = optionsHolder.Options;

    protected override string ProcessorName => "OutboxProcessor";
    protected override TimeSpan PollingInterval => _options.PollingInterval;
    protected override TimeSpan RestartDelay => _options.RestartDelay;
    protected override TimeSpan LockAcquireTimeout => _options.LockAcquireTimeout;
    protected override string LockName => _options.LockName;

    protected override async Task ProcessBatchesAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            using var batchScope = serviceScopeFactory.CreateScope();
            var processor = batchScope.ServiceProvider.GetRequiredService<
                OutboxMessageProcessor<TDbContext>
            >();

            LogCheckingOutboxUnsent(logger);
            var processedCount = await processor.ProcessBatchAsync(
                includeStuckMessageDetection: true,
                cancellationToken
            );

            if (processedCount == 0)
            {
                return;
            }
        }
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Debug,
        Message = "Checking outbox for unsent messages"
    )]
    private static partial void LogCheckingOutboxUnsent(ILogger logger);
}
