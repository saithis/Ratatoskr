using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Ratatoskr.Core;

namespace Ratatoskr.EfCore.Internal;

/// <summary>
/// Core inbox message processing logic shared between production and testing.
/// For each pending <see cref="InboxHandlerStatusEntity"/>, resolves the corresponding handler
/// from the <see cref="ChannelHandlerRegistry"/> and invokes it. Handles exponential backoff and stuck message detection.
/// </summary>
internal class InboxMessageProcessor<TDbContext>(
    TDbContext dbContext,
    HandlerInvoker handlerInvoker,
    ChannelHandlerRegistry channelHandlerRegistry,
    TimeProvider timeProvider,
    InboxOptionsHolder<TDbContext> optionsHolder,
    IEnumerable<IMessageActivityObserver> observers,
    IMessageSerializerResolver serializerResolver,
    ILogger<InboxMessageProcessor<TDbContext>> logger
)
    where TDbContext : DbContext, IInboxDbContext
{
    private readonly InboxOptions _options = optionsHolder.Options;

    /// <summary>
    /// Processes a single batch of pending handler statuses.
    /// Returns the number of handler statuses picked up in the batch (0 means no work found).
    /// </summary>
    /// <remarks>
    /// Each DbContext type is expected to have its own database, so queries naturally return
    /// only data for that database. Handler lookup by key is global (across all DbContext types),
    /// so correctness is maintained even if databases overlap.
    /// </remarks>
    public async Task<int> ProcessBatchAsync(
        bool includeStuckMessageDetection,
        CancellationToken cancellationToken
    )
    {
        var now = timeProvider.GetUtcNow();

        var query = dbContext
            .Set<InboxHandlerStatusEntity>()
            .Where(s =>
                s.CompletedAt == null
                && !s.IsPoisoned
                && (s.NextAttemptAt == null || s.NextAttemptAt <= now)
            );

        if (includeStuckMessageDetection)
        {
            var stuckThreshold = now - _options.StuckMessageThreshold;
            query = query.Where(s =>
                s.ProcessingStartedAt == null || s.ProcessingStartedAt < stuckThreshold
            );
        }
        else
        {
            query = query.Where(s => s.ProcessingStartedAt == null);
        }

        var statuses = await query
            .OrderBy(s => s.MessageId)
            .Take(_options.BatchSize)
            .ToArrayAsync(cancellationToken);

        if (statuses.Length == 0)
        {
            return 0;
        }

        InboxMessageProcessorLog.FoundStatusesToDeliver(logger, statuses.Length);

        var messageIds = statuses
            .Select(s => s.MessageId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var messages = await dbContext
            .Set<InboxMessageEntity>()
            .Where(m => messageIds.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, cancellationToken);

        var claimed = await MarkStatusesAsProcessingAsync(statuses, cancellationToken);
        if (claimed == null)
        {
            return 0;
        }
        statuses = claimed;

        InboxTelemetry.RecordBatchSize(statuses.Length);
        var batchStartTimestamp = Stopwatch.GetTimestamp();

        foreach (var status in statuses)
        {
            if (!await ProcessStatusAsync(status, messages, cancellationToken))
            {
                break;
            }
        }

        InboxTelemetry.RecordBatchDuration(batchStartTimestamp);
        return statuses.Length;
    }

    private async Task<InboxHandlerStatusEntity[]?> MarkStatusesAsProcessingAsync(
        InboxHandlerStatusEntity[] statuses,
        CancellationToken cancellationToken
    )
    {
        foreach (var status in statuses)
        {
            status.MarkAsProcessing(timeProvider);
        }

        while (true)
        {
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                return statuses;
            }
            catch (DbUpdateConcurrencyException ex)
            {
                var conflictIds = new HashSet<Guid>();
                foreach (var entry in ex.Entries)
                {
                    conflictIds.Add(((InboxHandlerStatusEntity)entry.Entity).Id);
                    await entry.ReloadAsync(cancellationToken);
                }

                InboxMessageProcessorLog.SkippedConflicts(logger, conflictIds.Count);

                statuses = [.. statuses.Where(s => !conflictIds.Contains(s.Id))];
                if (statuses.Length == 0)
                {
                    return null;
                }
            }
        }
    }

    /// <summary>Returns false to signal the caller should stop the batch loop (cancellation).</summary>
    private async Task<bool> ProcessStatusAsync(
        InboxHandlerStatusEntity status,
        Dictionary<string, InboxMessageEntity> messages,
        CancellationToken cancellationToken
    )
    {
        if (!messages.TryGetValue(status.MessageId, out var inboxMessage))
        {
            InboxMessageProcessorLog.MessageNotFound(logger, status.MessageId, status.Id);
            status.MarkAsPoisoned("InboxMessage record not found -- likely deleted.");
            InboxTelemetry.RecordPoisoned();
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }

        MessageProperties props;
        try
        {
            props = inboxMessage.GetProperties();
        }
        catch (Exception ex)
        {
            InboxMessageProcessorLog.DeserializationFailed(logger, status.MessageId, status.Id, ex);
            status.MarkAsPoisoned($"Properties deserialization failed: {ex.Message}");
            InboxTelemetry.RecordPoisoned();
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }

        var registration = channelHandlerRegistry.GetInboxRegistrationByKey(status.HandlerKey);
        if (registration == null)
        {
            InboxMessageProcessorLog.HandlerKeyNotRegistered(logger, status.HandlerKey, status.Id);
            status.MarkAsPoisoned(
                $"Handler key '{status.HandlerKey}' is not registered. The handler may have been removed or renamed."
            );
            InboxTelemetry.RecordPoisoned();
            await dbContext.SaveChangesAsync(cancellationToken);
            await observers.NotifyAsync(
                new MessageActivity
                {
                    Stage = MessageStage.InboxPoisoned,
                    Properties = props,
                    SerializedBody = inboxMessage.Content,
                    TransportName = inboxMessage.TransportName,
                    Timestamp = timeProvider.GetUtcNow(),
                },
                logger
            );
            return true;
        }

        var (shouldContinue, handlerException, deliveredMessage) = await InvokeHandlerAsync(
            status,
            inboxMessage,
            registration,
            props,
            cancellationToken
        );
        if (!shouldContinue)
            return false;

        await dbContext.SaveChangesAsync(CancellationToken.None);

        await observers.NotifyAsync(
            new MessageActivity
            {
                Stage = MessageStage.InboxDispatched,
                Properties = props,
                SerializedBody = inboxMessage.Content,
                Message = deliveredMessage,
                MessageType = registration.MessageType,
                TransportName = inboxMessage.TransportName,
                IsSuccess = handlerException == null,
                Exception = handlerException,
                Timestamp = timeProvider.GetUtcNow(),
            },
            logger
        );

        if (status.IsPoisoned)
        {
            InboxTelemetry.RecordPoisoned();
            await observers.NotifyAsync(
                new MessageActivity
                {
                    Stage = MessageStage.InboxPoisoned,
                    Properties = props,
                    SerializedBody = inboxMessage.Content,
                    TransportName = inboxMessage.TransportName,
                    Exception = handlerException,
                    Timestamp = timeProvider.GetUtcNow(),
                },
                logger
            );
        }

        return true;
    }

    private async Task<(
        bool ShouldContinue,
        Exception? HandlerException,
        object? DeliveredMessage
    )> InvokeHandlerAsync(
        InboxHandlerStatusEntity status,
        InboxMessageEntity inboxMessage,
        ChannelHandlerRegistration registration,
        MessageProperties props,
        CancellationToken cancellationToken
    )
    {
        Activity? deliverActivity = null;
        Exception? handlerException = null;
        object? deliveredMessage = null;
        try
        {
            deliverActivity = InboxTelemetry.StartDeliverActivity(props, status.HandlerKey);

            var serializer = serializerResolver.GetSerializer(registration.MessageType);
            deliveredMessage =
                serializer.Deserialize(inboxMessage.Content, registration.MessageType)
                ?? throw new InvalidOperationException(
                    $"Deserialized message of type '{registration.MessageType.Name}' was null."
                );

            await handlerInvoker.InvokeAsync(
                registration.HandlerType,
                deliveredMessage,
                props,
                cancellationToken,
                _options.HandlerTimeout
            );

            status.MarkAsCompleted(timeProvider);
            InboxTelemetry.RecordDelivered(success: true);
            InboxMessageProcessorLog.HandlerCompleted(logger, status.HandlerKey, status.MessageId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            InboxMessageProcessorLog.HandlerInterrupted(
                logger,
                status.HandlerKey,
                status.MessageId
            );
            deliverActivity?.Dispose();
            return (false, null, null);
        }
        catch (Exception ex)
        {
            handlerException = ex;
            deliverActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            InboxTelemetry.RecordDelivered(success: false);
            InboxMessageProcessorLog.HandlerFailed(
                logger,
                status.HandlerKey,
                status.MessageId,
                status.ErrorCount + 1,
                ex
            );
            status.MarkAsFailed(
                ex.Message,
                timeProvider,
                _options.MaxRetries,
                _options.MaxRetryDelay
            );

            if (status.IsPoisoned)
            {
                InboxMessageProcessorLog.HandlerPoisoned(
                    logger,
                    status.HandlerKey,
                    status.MessageId,
                    status.ErrorCount,
                    ex.Message
                );
            }
        }
        finally
        {
            deliverActivity?.Dispose();
        }

        return (true, handlerException, deliveredMessage);
    }
}

internal static partial class InboxMessageProcessorLog
{
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Found {Count} inbox handler status(es) to deliver"
    )]
    public static partial void FoundStatusesToDeliver(ILogger logger, int count);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Skipped {ConflictCount} inbox handler status(es) already claimed by another worker"
    )]
    public static partial void SkippedConflicts(ILogger logger, int conflictCount);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "InboxMessage '{MessageId}' not found for handler status '{StatusId}'. Poisoning status."
    )]
    public static partial void MessageNotFound(ILogger logger, string messageId, Guid statusId);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Failed to deserialize properties for InboxMessage '{MessageId}'. Poisoning status '{StatusId}'."
    )]
    public static partial void DeserializationFailed(
        ILogger logger,
        string messageId,
        Guid statusId,
        Exception ex
    );

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Handler key '{HandlerKey}' is no longer registered. Poisoning status '{StatusId}'."
    )]
    public static partial void HandlerKeyNotRegistered(
        ILogger logger,
        string handlerKey,
        Guid statusId
    );

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Inbox handler '{HandlerKey}' completed for message '{MessageId}'"
    )]
    public static partial void HandlerCompleted(
        ILogger logger,
        string handlerKey,
        string messageId
    );

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Inbox handler '{HandlerKey}' for message '{MessageId}' interrupted by cancellation, will be retried via stuck detection"
    )]
    public static partial void HandlerInterrupted(
        ILogger logger,
        string handlerKey,
        string messageId
    );

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Inbox handler '{HandlerKey}' failed for message '{MessageId}', attempt {Attempt}"
    )]
    public static partial void HandlerFailed(
        ILogger logger,
        string handlerKey,
        string messageId,
        int attempt,
        Exception ex
    );

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Inbox handler '{HandlerKey}' for message '{MessageId}' has been poisoned after {Attempts} failed attempts. Last error: {Error}"
    )]
    public static partial void HandlerPoisoned(
        ILogger logger,
        string handlerKey,
        string messageId,
        int attempts,
        string error
    );
}
