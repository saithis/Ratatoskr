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
    ILogger<OutboxMessageProcessor<TDbContext>> logger
)
    where TDbContext : DbContext, IOutboxDbContext
{
    private readonly OutboxOptions _options = optionsHolder.Options;
    private Dictionary<string, IMessageSender> _senderMap = senders.ToDictionary(x =>
        x.TransportName
    );

    /// <summary>
    /// Processes a single batch of outbox messages.
    /// Returns the number of messages successfully processed.
    /// </summary>
    public async Task<int> ProcessBatchAsync(
        bool includeStuckMessageDetection,
        CancellationToken cancellationToken
    )
    {
        var now = timeProvider.GetUtcNow();

        var query = dbContext
            .Set<OutboxMessageEntity>()
            .Where(x =>
                x.ProcessedAt == null
                && !x.IsPoisoned
                && (x.NextAttemptAt == null || x.NextAttemptAt <= now)
            );

        if (includeStuckMessageDetection)
        {
            var stuckThreshold = now - _options.StuckMessageThreshold;
            query = query.Where(x =>
                x.ProcessingStartedAt == null || x.ProcessingStartedAt < stuckThreshold
            );
        }
        else
        {
            // Without stuck recovery, still exclude rows another worker has already claimed; otherwise two
            // concurrent processors can both pass MarkAsProcessing/SaveChanges in sequence and duplicate-send.
            query = query.Where(x => x.ProcessingStartedAt == null);
        }

        var messages = await query
            .OrderBy(x => x.CreatedAt)
            .Take(_options.BatchSize)
            .ToArrayAsync(cancellationToken);

        telemetry.RecordBatchSize(messages.Length);

        OutboxMessageProcessorLog.FoundMessagesToSend(logger, messages.Length);

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

                OutboxMessageProcessorLog.SkippedConflicts(logger, conflictIds.Count);

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
                OutboxMessageProcessorLog.DeserializationFailed(logger, message.Id, ex);
                message.MarkAsPoisoned(ex.Message, timeProvider);
            }

            if (sendException == null && props != null)
            {
                if (!_senderMap.TryGetValue(message.TransportName, out var targetSender))
                {
                    OutboxMessageProcessorLog.NoSenderFound(
                        logger,
                        message.TransportName,
                        message.Id
                    );
                    message.MarkAsPoisoned(
                        $"No sender found for transport '{message.TransportName}'",
                        timeProvider
                    );
                }
                else
                {
                    try
                    {
                        using var activity = telemetry.StartCreateActivity(props);

                        OutboxMessageProcessorLog.ProcessingMessage(
                            logger,
                            message.Id,
                            message.TransportName
                        );

                        if (_options.SendTimeout.HasValue)
                        {
                            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(
                                cancellationToken
                            );
                            timeoutCts.CancelAfter(_options.SendTimeout.Value);
                            await targetSender.SendAsync(message.Content, props, timeoutCts.Token);
                        }
                        else
                        {
                            await targetSender.SendAsync(message.Content, props, cancellationToken);
                        }
                        message.MarkAsProcessed(timeProvider);
                    }
                    catch (OperationCanceledException)
                        when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception e)
                    {
                        sendException = e;
                        OutboxMessageProcessorLog.SendFailed(
                            logger,
                            message.Id,
                            message.ErrorCount + 1,
                            e
                        );
                        message.PublishFailed(
                            e.Message,
                            timeProvider,
                            _options.MaxRetries,
                            _options.MaxRetryDelay
                        );
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

                OutboxMessageProcessorLog.SkippedConflictsDuringSave(logger, conflictIds.Count);

                if (conflictIds.Contains(message.Id))
                    continue;
            }

            // Record telemetry only after persistence succeeds
            if (sendException == null && props != null)
            {
                processedCount++;
                telemetry.RecordProcessed(success: true);
            }
            else
            {
                telemetry.RecordProcessed(success: false);

                if (message.IsPoisoned)
                {
                    telemetry.RecordPoisoned();
                    OutboxMessageProcessorLog.MessagePoisoned(
                        logger,
                        message.Id,
                        message.TransportName,
                        message.ErrorCount,
                        sendException?.Message ?? string.Empty
                    );
                }
            }

            if (sendException == null && props != null)
            {
                await observers.NotifyAsync(
                    new MessageActivity
                    {
                        Stage = MessageStage.OutboxSent,
                        Properties = props,
                        SerializedBody = message.Content,
                        TransportName = message.TransportName,
                        Timestamp = timeProvider.GetUtcNow(),
                    },
                    logger
                );
            }

            if (message.IsPoisoned)
            {
                await observers.NotifyAsync(
                    new MessageActivity
                    {
                        Stage = MessageStage.OutboxPoisoned,
                        Properties = props ?? new MessageProperties(),
                        SerializedBody = message.Content,
                        TransportName = message.TransportName,
                        Exception = sendException,
                        Timestamp = timeProvider.GetUtcNow(),
                    },
                    logger
                );
            }
        }

        telemetry.RecordBatchDuration(batchStartTimestamp);

        return processedCount;
    }
}

internal static partial class OutboxMessageProcessorLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Found {Count} messages to send")]
    public static partial void FoundMessagesToSend(ILogger logger, int count);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Skipped {ConflictCount} outbox message(s) already claimed by another worker"
    )]
    public static partial void SkippedConflicts(ILogger logger, int conflictCount);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Skipped {ConflictCount} outbox message(s) already claimed by another worker during save"
    )]
    public static partial void SkippedConflictsDuringSave(ILogger logger, int conflictCount);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Failed to deserialize properties for message '{Id}' - treating as poison"
    )]
    public static partial void DeserializationFailed(ILogger logger, Guid id, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "No sender registered for transport '{Transport}' on message '{Id}' - treating as poison"
    )]
    public static partial void NoSenderFound(ILogger logger, string transport, Guid id);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Processing message '{Id}' for transport '{Transport}'"
    )]
    public static partial void ProcessingMessage(ILogger logger, Guid id, string transport);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failed to send message '{Id}', attempt {Attempt}"
    )]
    public static partial void SendFailed(ILogger logger, Guid id, int attempt, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Outbox message '{Id}' for transport '{Transport}' has been poisoned after {Attempts} failed attempts. Last error: {Error}"
    )]
    public static partial void MessagePoisoned(
        ILogger logger,
        Guid id,
        string transport,
        int attempts,
        string error
    );
}
