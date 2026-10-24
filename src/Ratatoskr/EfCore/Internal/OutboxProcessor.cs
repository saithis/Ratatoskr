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
) : PollingBackgroundService(
        optionsHolder.Options,
        distributedLockProvider,
        timeProvider,
        logger
    )
    where TDbContext : DbContext, IRatatoskrDbContext
{
    protected override string ProcessorName => "OutboxProcessor";

    protected override async Task ProcessBatchesAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            using var batchScope = serviceScopeFactory.CreateScope();
            var processor = batchScope.ServiceProvider.GetRequiredService<
                OutboxMessageProcessor<TDbContext>
            >();

            LogCheckingOutboxUnsent(logger);
            var result = await processor.ProcessBatchAsync(
                includeStuckMessageDetection: true,
                cancellationToken
            );

            // Keep going while there is work, even if every message in the batch failed or was
            // poisoned: failed messages are backed off and poisoned ones excluded, so the next
            // batch picks up different messages.
            if (result.Claimed == 0)
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
