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
    OutboxOptionsHolder<TDbContext> optionsHolder,
    IEnumerable<IMessageActivityObserver> observers,
    ILogger<OutboxMessageProcessor<TDbContext>> logger
)
    where TDbContext : DbContext, IRatatoskrDbContext
{
    private readonly OutboxOptions _options = optionsHolder.Options;
    private readonly Dictionary<string, IMessageSender> _senderMap = senders.ToDictionary(
        x => x.TransportName,
        StringComparer.Ordinal
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
                && (x.ScheduledAt == null || x.ScheduledAt <= now)
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

        OutboxTelemetry.RecordBatchSize(messages.Length);

        OutboxMessageProcessorLog.FoundMessagesToSend(logger, messages.Length);

        if (messages.Length == 0)
        {
            return 0;
        }

        messages = await ProcessingConcurrency.ClaimAsync(
            dbContext,
            messages,
            timeProvider,
            count => OutboxMessageProcessorLog.SkippedConflicts(logger, count),
            cancellationToken
        );
        if (messages.Length == 0)
        {
            return 0;
        }

        var processedCount = 0;
        var batchStartTimestamp = Stopwatch.GetTimestamp();

        foreach (var message in messages)
        {
            var outcome = await ProcessMessageAsync(message, cancellationToken);
            if (outcome == ProcessOutcome.Sent)
            {
                processedCount++;
            }
        }

        OutboxTelemetry.RecordBatchDuration(batchStartTimestamp);
        return processedCount;
    }

    private async Task<ProcessOutcome> ProcessMessageAsync(
        OutboxMessageEntity message,
        CancellationToken cancellationToken
    )
    {
        var (props, sendException) = await TrySendAsync(message, cancellationToken);

        if (
            !await ProcessingConcurrency.SaveSkippingConflictsAsync(
                dbContext,
                message,
                count => OutboxMessageProcessorLog.SkippedConflictsDuringSave(logger, count)
            )
        )
        {
            return ProcessOutcome.Conflict;
        }

        var outcome = ProcessOutcome.Failed;
        if (message.ProcessedAt != null)
        {
            outcome = ProcessOutcome.Sent;
        }
        else if (message.IsPoisoned)
        {
            outcome = ProcessOutcome.Poisoned;
        }

        await RecordAndNotifyAsync(message, outcome, props, sendException);
        return outcome;
    }

    /// <summary>
    /// Sends the message and updates its state in memory (processed, failed or poisoned).
    /// Returns the deserialized properties (null if they could not be read) and the exception
    /// that caused the failure, if any.
    /// </summary>
    private async Task<(MessageProperties? Properties, Exception? Exception)> TrySendAsync(
        OutboxMessageEntity message,
        CancellationToken cancellationToken
    )
    {
        MessageProperties props;
        try
        {
            props = message.GetProperties();
        }
        catch (Exception ex)
        {
            OutboxMessageProcessorLog.DeserializationFailed(logger, message.Id, ex);
            message.MarkAsPoisoned(ex.Message, timeProvider);
            return (null, ex);
        }

        if (!_senderMap.TryGetValue(message.TransportName, out var targetSender))
        {
            OutboxMessageProcessorLog.NoSenderFound(logger, message.TransportName, message.Id);
            message.MarkAsPoisoned(
                $"No sender found for transport '{message.TransportName}'",
                timeProvider
            );
            return (props, null);
        }

        try
        {
            using var activity = OutboxTelemetry.StartCreateActivity(props);
            OutboxMessageProcessorLog.ProcessingMessage(logger, message.Id, message.TransportName);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken
            );
            if (_options.SendTimeout.HasValue)
            {
                timeoutCts.CancelAfter(_options.SendTimeout.Value);
            }

            await targetSender.SendAsync(message.Content, props, timeoutCts.Token);
            message.MarkAsProcessed(timeProvider);
            return (props, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            OutboxMessageProcessorLog.SendFailed(logger, message.Id, message.ErrorCount + 1, e);
            message.PublishFailed(
                e.Message,
                timeProvider,
                _options.MaxRetries,
                _options.MaxRetryDelay
            );
            return (props, e);
        }
    }

    private async Task RecordAndNotifyAsync(
        OutboxMessageEntity message,
        ProcessOutcome outcome,
        MessageProperties? props,
        Exception? sendException
    )
    {
        OutboxTelemetry.RecordProcessed(success: outcome == ProcessOutcome.Sent);

        if (outcome == ProcessOutcome.Sent)
        {
            await observers.NotifyAsync(
                new MessageActivity
                {
                    Stage = MessageStage.OutboxSent,
                    Properties = props!,
                    SerializedBody = message.Content,
                    TransportName = message.TransportName,
                    Timestamp = timeProvider.GetUtcNow(),
                },
                logger
            );
        }
        else if (outcome == ProcessOutcome.Poisoned)
        {
            OutboxTelemetry.RecordPoisoned();
            OutboxMessageProcessorLog.MessagePoisoned(
                logger,
                message.Id,
                message.TransportName,
                message.ErrorCount,
                message.Error
            );
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

    private enum ProcessOutcome
    {
        Sent = 0,

        /// <summary>The send failed and will be retried after a backoff.</summary>
        Failed = 1,
        Poisoned = 2,

        /// <summary>The message was modified concurrently (for example by the management API) and was skipped.</summary>
        Conflict = 3,
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
