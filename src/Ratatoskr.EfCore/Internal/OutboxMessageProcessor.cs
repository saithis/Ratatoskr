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
    OutboxOptionsHolder<TDbContext> optionsHolder,
    IEnumerable<IMessageActivityObserver> observers,
    ILogger<OutboxMessageProcessor<TDbContext>> logger)
    where TDbContext : DbContext, IOutboxDbContext
{
    private readonly OutboxOptions _options = optionsHolder.Options;
    private Dictionary<string, IMessageSender> _senderMap = senders.ToDictionary(x => x.TransportName);

    /// <summary>
    /// Processes a single batch of outbox messages.
    /// Returns the number of messages successfully processed.
    /// </summary>
    public async Task<int> ProcessBatchAsync(
        bool includeStuckMessageDetection,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        var query = dbContext.Set<OutboxMessageEntity>()
            .Where(x => x.ProcessedAt == null
                     && !x.IsPoisoned
                     && (x.NextAttemptAt == null || x.NextAttemptAt <= now));

        if (includeStuckMessageDetection)
        {
            var stuckThreshold = now - _options.StuckMessageThreshold;
            query = query.Where(x => x.ProcessingStartedAt == null || x.ProcessingStartedAt < stuckThreshold);
        }

        var messages = await query
            .OrderBy(x => x.CreatedAt)
            .Take(_options.BatchSize)
            .ToArrayAsync(cancellationToken);

        telemetry.RecordBatchSize(messages.Length);

        logger.LogInformation("Found {Count} messages to send", messages.Length);

        if (messages.Length == 0)
            return 0;

        foreach (var message in messages)
        {
            message.MarkAsProcessing(timeProvider);
        }

        while (true)
        {
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                break;
            }
            catch (DbUpdateConcurrencyException ex)
            {
                var conflictIds = new HashSet<Guid>();
                foreach (var entry in ex.Entries)
                {
                    conflictIds.Add(((OutboxMessageEntity)entry.Entity).Id);
                    await entry.ReloadAsync(cancellationToken);
                }

                logger.LogDebug(
                    "Skipped {ConflictCount} outbox message(s) already claimed by another worker",
                    conflictIds.Count);

                messages = messages.Where(m => !conflictIds.Contains(m.Id)).ToArray();
                if (messages.Length == 0)
                    return 0;
            }
        }

        var processedCount = 0;
        var batchStartTimestamp = Stopwatch.GetTimestamp();

        foreach (var message in messages)
        {
            MessageProperties? props = null;
            Exception? sendException = null;

            try
            {
                props = message.GetProperties();
            }
            catch (Exception ex)
            {
                sendException = ex;
                logger.LogWarning(ex, "Failed to deserialize properties for message '{Id}'", message.Id);
                message.PublishFailed(ex.Message, timeProvider,
                    _options.MaxRetries, _options.MaxRetryDelay);
                telemetry.RecordProcessed(success: false);

                if (message.IsPoisoned)
                {
                    telemetry.RecordPoisoned();
                    logger.LogError("Outbox message '{Id}' for transport '{Transport}' has been poisoned after {Attempts} failed attempts. Last error: {Error}",
                        message.Id, message.TransportName, message.ErrorCount, ex.Message);
                }
            }

            if (sendException == null && props != null)
            {
                try
                {
                    using var activity = telemetry.StartCreateActivity(props);

                    logger.LogInformation("Processing message '{Id}' for transport '{Transport}'", message.Id, message.TransportName);

                    var targetSender = _senderMap.GetValueOrDefault(message.TransportName)
                                       ?? throw new InvalidOperationException($"No sender found for transport '{message.TransportName}'");

                    if (_options.SendTimeout.HasValue)
                    {
                        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        timeoutCts.CancelAfter(_options.SendTimeout.Value);
                        await targetSender.SendAsync(message.Content, props, timeoutCts.Token);
                    }
                    else
                    {
                        await targetSender.SendAsync(message.Content, props, cancellationToken);
                    }
                    message.MarkAsProcessed(timeProvider);
                    processedCount++;
                    telemetry.RecordProcessed(success: true);
                }
                catch (Exception e)
                {
                    sendException = e;
                    logger.LogWarning(e, "Failed to send message '{Id}', attempt {Attempt}",
                        message.Id, message.ErrorCount + 1);
                    message.PublishFailed(e.Message, timeProvider,
                        _options.MaxRetries, _options.MaxRetryDelay);
                    telemetry.RecordProcessed(success: false);

                    if (message.IsPoisoned)
                    {
                        telemetry.RecordPoisoned();
                        logger.LogError("Outbox message '{Id}' for transport '{Transport}' has been poisoned after {Attempts} failed attempts. Last error: {Error}",
                            message.Id, message.TransportName, message.ErrorCount, e.Message);
                    }
                }
            }

            try
            {
                await dbContext.SaveChangesAsync(CancellationToken.None);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                var conflictIds = new HashSet<Guid>();
                foreach (var entry in ex.Entries)
                {
                    conflictIds.Add(((OutboxMessageEntity)entry.Entity).Id);
                    await entry.ReloadAsync(CancellationToken.None);
                }

                logger.LogDebug(
                    "Skipped {ConflictCount} outbox message(s) already claimed by another worker during save",
                    conflictIds.Count);

                if (conflictIds.Contains(message.Id))
                    continue;
            }

            if (sendException == null && props != null)
            {
                await observers.NotifyAsync(new MessageActivity
                {
                    Stage = MessageStage.OutboxSent,
                    Properties = props,
                    SerializedBody = message.Content,
                    TransportName = message.TransportName,
                    Timestamp = timeProvider.GetUtcNow(),
                }, logger);
            }

            if (message.IsPoisoned && props != null)
            {
                await observers.NotifyAsync(new MessageActivity
                {
                    Stage = MessageStage.OutboxPoisoned,
                    Properties = props,
                    SerializedBody = message.Content,
                    TransportName = message.TransportName,
                    Exception = sendException,
                    Timestamp = timeProvider.GetUtcNow(),
                }, logger);
            }
        }

        telemetry.RecordBatchDuration(batchStartTimestamp);

        return processedCount;
    }
}
