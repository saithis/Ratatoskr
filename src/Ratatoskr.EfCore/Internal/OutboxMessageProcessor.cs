using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Ratatoskr.Core;

namespace Ratatoskr.EfCore.Internal;

/// <summary>
/// Core outbox message processing logic shared between production and testing.
/// This ensures tests use the EXACT SAME logic as production.
/// </summary>
internal class OutboxMessageProcessor<TDbContext>(
    TDbContext dbContext,
    IEnumerable<IMessageSender> senders,
    OutboxTelemetry telemetry,
    TimeProvider timeProvider,
    OutboxOptions options,
    IEnumerable<IMessageActivityObserver> observers,
    ILogger logger)
    where TDbContext : DbContext, IOutboxDbContext
{
    private Dictionary<string, IMessageSender> _senderMap = senders.ToDictionary(x => x.TransportName);

    /// <summary>
    /// Processes a single batch of outbox messages.
    /// Returns the number of messages successfully processed.
    /// </summary>
    /// <param name="includeStuckMessageDetection">Whether to check for stuck messages (only needed in production background processing)</param>
    public async Task<int> ProcessBatchAsync(
        bool includeStuckMessageDetection,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        // Build the query for pending messages
        var query = dbContext.Set<OutboxMessageEntity>()
            .Where(x => x.ProcessedAt == null
                     && !x.IsPoisoned
                     && (x.NextAttemptAt == null || x.NextAttemptAt <= now));

        // Add stuck message detection if needed (only for production background processing)
        if (includeStuckMessageDetection)
        {
            var stuckThreshold = now - options.StuckMessageThreshold;
            query = query.Where(x => x.ProcessingStartedAt == null || x.ProcessingStartedAt < stuckThreshold);
        }

        var messages = await query
            .OrderBy(x => x.CreatedAt)
            .Take(options.BatchSize)
            .ToArrayAsync(cancellationToken);

        telemetry.RecordBatchSize(messages.Length);

        logger.LogInformation("Found {Count} messages to send", messages.Length);

        if (messages.Length == 0)
            return 0;

        // Mark all as processing before sending
        foreach (var message in messages)
        {
            message.MarkAsProcessing(timeProvider);
        }
        await dbContext.SaveChangesAsync(cancellationToken);

        var processedCount = 0;
        var batchStartTimestamp = Stopwatch.GetTimestamp();

        // Process each message with error handling
        foreach (var message in messages)
        {
            MessageProperties? sentProps = null;

            try
            {
                var props = message.GetProperties();

                using var activity = telemetry.StartCreateActivity(props);

                logger.LogInformation("Processing message '{Id}' for transport '{Transport}'", message.Id, message.TransportName);

                // Find the matching sender for this outbox entry's transport
                var targetSender = _senderMap.GetValueOrDefault(message.TransportName)
                                   ?? throw new InvalidOperationException($"No sender found for transport '{message.TransportName}'");

                // When SendTimeout is configured, wrap in a linked CTS that fires after the timeout.
                if (options.SendTimeout.HasValue)
                {
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeoutCts.CancelAfter(options.SendTimeout.Value);
                    await targetSender.SendAsync(message.Content, props, timeoutCts.Token);
                }
                else
                {
                    await targetSender.SendAsync(message.Content, props, cancellationToken);
                }
                message.MarkAsProcessed(timeProvider);
                processedCount++;
                telemetry.RecordProcessed(success: true);
                sentProps = props;
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Failed to send message '{Id}', attempt {Attempt}",
                    message.Id, message.ErrorCount + 1);
                message.PublishFailed(e.Message, timeProvider,
                    options.MaxRetries, options.MaxRetryDelay);
                telemetry.RecordProcessed(success: false);

                if (message.IsPoisoned)
                {
                    logger.LogError("Outbox message '{Id}' for transport '{Transport}' has been poisoned after {Attempts} failed attempts. Last error: {Error}",
                        message.Id, message.TransportName, message.ErrorCount, e.Message);
                }
            }

            // Persist each message's state immediately so progress isn't lost on crash
            await dbContext.SaveChangesAsync(CancellationToken.None);

            if (sentProps != null)
            {
                await observers.NotifyAsync(new OutboxMessageSent
                {
                    Properties = sentProps,
                    SerializedBody = message.Content,
                    TransportName = message.TransportName,
                    Timestamp = timeProvider.GetUtcNow(),
                }, logger);
            }
        }

        telemetry.RecordBatchDuration(batchStartTimestamp);

        return processedCount;
    }
}
