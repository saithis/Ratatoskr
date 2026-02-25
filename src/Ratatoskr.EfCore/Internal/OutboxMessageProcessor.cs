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
    TimeProvider timeProvider,
    OutboxOptions options,
    IEnumerable<IMessageActivityObserver> observers,
    ILogger logger)
    where TDbContext : DbContext, IOutboxDbContext
{
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

        RatatoskrDiagnostics.OutboxBatchSize.Record(messages.Length);

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

                // Extract parent tracing context
                ActivityContext.TryParse(props.TraceParent, props.TraceState, out var parentContext);

                using var activity = RatatoskrDiagnostics.ActivitySource.StartActivity(
                    "Ratatoskr.OutboxProcess",
                    ActivityKind.Producer,
                    parentContext);

                if (activity != null)
                {
                    // https://opentelemetry.io/docs/specs/semconv/messaging/messaging-spans/#messaging-attributes
                    activity.SetTag("messaging.system", "ratatoskr");
                    activity.SetTag("messaging.message.id", props.Id);
                }

                logger.LogInformation("Processing message '{Id}' for transport '{Transport}'", message.Id, message.TransportName);

                // Find the matching sender for this outbox entry's transport
                var targetSender = senders.FirstOrDefault(s => s.TransportName == message.TransportName)
                      ?? throw new InvalidOperationException($"No sender found for transport '{message.TransportName}'");

                await targetSender.SendAsync(message.Content, props, cancellationToken);
                message.MarkAsProcessed(timeProvider);
                processedCount++;
                RatatoskrDiagnostics.OutboxProcessCount.Add(1, new TagList { { "status", "success" } });
                sentProps = props;
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Failed to send message '{Id}', attempt {Attempt}",
                    message.Id, message.ErrorCount + 1);
                message.PublishFailed(e.Message, timeProvider,
                    options.MaxRetries, options.MaxRetryDelay);
                RatatoskrDiagnostics.OutboxProcessCount.Add(1, new TagList { { "status", "failure" } });
            }

            if (sentProps != null)
            {
                foreach (var observer in observers)
                {
                    try
                    {
                        await observer.OnMessageActivity(new MessageActivity
                        {
                            Stage = MessageStage.OutboxSent,
                            Properties = sentProps,
                            SerializedBody = message.Content,
                            Timestamp = timeProvider.GetUtcNow(),
                        });
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Message activity observer failed at the {Stage} stage", MessageStage.OutboxSent);
                    }
                }
            }
        }

        RatatoskrDiagnostics.OutboxProcessDuration.Record(Stopwatch.GetElapsedTime(batchStartTimestamp).TotalMilliseconds);

        // Save all changes (both successful and failed)
        await dbContext.SaveChangesAsync(CancellationToken.None);
        
        return processedCount;
    }
}
