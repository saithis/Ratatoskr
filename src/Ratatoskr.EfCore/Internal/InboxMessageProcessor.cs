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
    InboxTelemetry telemetry,
    TimeProvider timeProvider,
    InboxOptionsHolder<TDbContext> optionsHolder,
    IEnumerable<IMessageActivityObserver> observers,
    IMessageSerializer messageSerializer,
    ILogger<InboxMessageProcessor<TDbContext>> logger)
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
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        var query = dbContext.Set<InboxHandlerStatusEntity>()
            .Where(s => s.CompletedAt == null
                     && !s.IsPoisoned
                     && (s.NextAttemptAt == null || s.NextAttemptAt <= now));

        if (includeStuckMessageDetection)
        {
            var stuckThreshold = now - _options.StuckMessageThreshold;
            query = query.Where(s => s.ProcessingStartedAt == null || s.ProcessingStartedAt < stuckThreshold);
        }

        var statuses = await query
            .OrderBy(s => s.MessageId)
            .Take(_options.BatchSize)
            .ToArrayAsync(cancellationToken);

        if (statuses.Length == 0)
            return 0;

        logger.LogInformation("Found {Count} inbox handler status(es) to deliver", statuses.Length);

        var messageIds = statuses.Select(s => s.MessageId).Distinct().ToArray();
        var messages = await dbContext.Set<InboxMessageEntity>()
            .Where(m => messageIds.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, cancellationToken);

        foreach (var status in statuses)
            status.MarkAsProcessing(timeProvider);

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
                    conflictIds.Add(((InboxHandlerStatusEntity)entry.Entity).Id);
                    await entry.ReloadAsync(cancellationToken);
                }

                logger.LogDebug(
                    "Skipped {ConflictCount} inbox handler status(es) already claimed by another worker",
                    conflictIds.Count);

                statuses = statuses.Where(s => !conflictIds.Contains(s.Id)).ToArray();
                if (statuses.Length == 0)
                    return 0;
            }
        }

        telemetry.RecordBatchSize(statuses.Length);

        var batchStartTimestamp = Stopwatch.GetTimestamp();

        foreach (var status in statuses)
        {
            if (!messages.TryGetValue(status.MessageId, out var inboxMessage))
            {
                logger.LogError("InboxMessage '{MessageId}' not found for handler status '{StatusId}'. Poisoning status.",
                    status.MessageId, status.Id);
                status.MarkAsPoisoned("InboxMessage record not found — likely deleted.", timeProvider);
                telemetry.RecordPoisoned();
                await dbContext.SaveChangesAsync(cancellationToken);
                continue;
            }

            MessageProperties props;
            try
            {
                props = inboxMessage.GetProperties();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to deserialize properties for InboxMessage '{MessageId}'. Poisoning status '{StatusId}'.",
                    status.MessageId, status.Id);
                status.MarkAsPoisoned($"Properties deserialization failed: {ex.Message}", timeProvider);
                telemetry.RecordPoisoned();
                await dbContext.SaveChangesAsync(cancellationToken);
                continue;
            }

            var registration = channelHandlerRegistry.GetInboxRegistrationByKey(status.HandlerKey);
            if (registration == null)
            {
                logger.LogWarning(
                    "Handler key '{HandlerKey}' is no longer registered. Poisoning status '{StatusId}'.",
                    status.HandlerKey, status.Id);
                status.MarkAsPoisoned(
                    $"Handler key '{status.HandlerKey}' is not registered. The handler may have been removed or renamed.",
                    timeProvider);
                telemetry.RecordPoisoned();
                await dbContext.SaveChangesAsync(cancellationToken);

                await observers.NotifyAsync(new MessageActivity
                {
                    Stage = MessageStage.InboxPoisoned,
                    Properties = props,
                    SerializedBody = inboxMessage.Content,
                    TransportName = inboxMessage.TransportName,
                    Timestamp = timeProvider.GetUtcNow(),
                }, logger);

                continue;
            }

            Activity? deliverActivity = null;
            Exception? handlerException = null;
            try
            {
                deliverActivity = telemetry.StartDeliverActivity(props, status.HandlerKey);

                var message = messageSerializer.Deserialize(inboxMessage.Content, registration.MessageType)
                              ?? throw new InvalidOperationException(
                                  $"Deserialized message of type '{registration.MessageType.Name}' was null.");

                await handlerInvoker.InvokeAsync(
                    registration.HandlerType, message, props,
                    cancellationToken, _options.HandlerTimeout);

                status.MarkAsCompleted(timeProvider);
                telemetry.RecordDelivered(success: true);

                logger.LogDebug("Inbox handler '{HandlerKey}' completed for message '{MessageId}'",
                    status.HandlerKey, status.MessageId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                logger.LogDebug(
                    "Inbox handler '{HandlerKey}' for message '{MessageId}' interrupted by cancellation, will be retried via stuck detection",
                    status.HandlerKey, status.MessageId);
                deliverActivity?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                handlerException = ex;
                deliverActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                telemetry.RecordDelivered(success: false);
                logger.LogWarning(ex,
                    "Inbox handler '{HandlerKey}' failed for message '{MessageId}', attempt {Attempt}",
                    status.HandlerKey, status.MessageId, status.ErrorCount + 1);
                status.MarkAsFailed(ex.Message, timeProvider, _options.MaxRetries, _options.MaxRetryDelay);

                if (status.IsPoisoned)
                {
                    logger.LogError("Inbox handler '{HandlerKey}' for message '{MessageId}' has been poisoned after {Attempts} failed attempts. Last error: {Error}",
                        status.HandlerKey, status.MessageId, status.ErrorCount, ex.Message);
                }
            }
            finally
            {
                deliverActivity?.Dispose();
            }

            await dbContext.SaveChangesAsync(CancellationToken.None);

            await observers.NotifyAsync(new MessageActivity
            {
                Stage = MessageStage.InboxDispatched,
                Properties = props,
                SerializedBody = inboxMessage.Content,
                TransportName = inboxMessage.TransportName,
                IsSuccess = handlerException == null,
                Exception = handlerException,
                Timestamp = timeProvider.GetUtcNow(),
            }, logger);

            if (status.IsPoisoned)
            {
                telemetry.RecordPoisoned();
                await observers.NotifyAsync(new MessageActivity
                {
                    Stage = MessageStage.InboxPoisoned,
                    Properties = props,
                    SerializedBody = inboxMessage.Content,
                    TransportName = inboxMessage.TransportName,
                    Exception = handlerException,
                    Timestamp = timeProvider.GetUtcNow(),
                }, logger);
            }
        }

        telemetry.RecordBatchDuration(batchStartTimestamp);

        return statuses.Length;
    }
}
