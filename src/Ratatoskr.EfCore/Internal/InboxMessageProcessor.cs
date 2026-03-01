using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ratatoskr.Core;

namespace Ratatoskr.EfCore.Internal;

/// <summary>
/// Core inbox message processing logic shared between production and testing.
/// For each pending <see cref="InboxHandlerStatusEntity"/>, resolves the corresponding handler
/// from DI and invokes it. Handles exponential backoff and stuck message detection.
/// </summary>
internal class InboxMessageProcessor<TDbContext>(
    TDbContext dbContext,
    IServiceScopeFactory scopeFactory,
    InboxHandlerRegistry handlerRegistry,
    TimeProvider timeProvider,
    InboxOptions options,
    IEnumerable<IMessageActivityObserver> observers,
    IMessageSerializer messageSerializer,
    ILogger logger)
    where TDbContext : DbContext, IInboxDbContext
{
    /// <summary>
    /// Processes a single batch of pending handler statuses.
    /// Returns the number of handler statuses picked up in the batch (0 means no work found).
    /// </summary>
    /// <param name="includeStuckMessageDetection">Whether to include stuck-message detection (needed in background processing).</param>
    public async Task<int> ProcessBatchAsync(
        bool includeStuckMessageDetection,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        // Query pending handler statuses
        var query = dbContext.Set<InboxHandlerStatusEntity>()
            .Where(s => s.CompletedAt == null
                     && !s.IsPoisoned
                     && (s.NextAttemptAt == null || s.NextAttemptAt <= now));

        if (includeStuckMessageDetection)
        {
            var stuckThreshold = now - options.StuckMessageThreshold;
            query = query.Where(s => s.ProcessingStartedAt == null || s.ProcessingStartedAt < stuckThreshold);
        }

        var statuses = await query
            .OrderBy(s => s.MessageId)
            .Take(options.BatchSize)
            .ToArrayAsync(cancellationToken);

        if (statuses.Length == 0)
            return 0;

        logger.LogInformation("Found {Count} inbox handler status(es) to deliver", statuses.Length);

        // Load all required messages in one query
        var messageIds = statuses.Select(s => s.MessageId).Distinct().ToArray();
        var messages = await dbContext.Set<InboxMessageEntity>()
            .Where(m => messageIds.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, cancellationToken);

        // Claim statuses with optimistic concurrency. If another worker already
        // claimed some of these rows, their Version column has changed and EF Core
        // throws DbUpdateConcurrencyException. We reload the conflicting entries
        // (which resets them to Unchanged) and retry with the remaining entries.
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

        RatatoskrDiagnostics.InboxBatchSize.Record(statuses.Length);

        var batchStartTimestamp = Stopwatch.GetTimestamp();

        foreach (var status in statuses)
        {
            if (!messages.TryGetValue(status.MessageId, out var inboxMessage))
            {
                logger.LogError("InboxMessage '{MessageId}' not found for handler status '{StatusId}'. Poisoning status.",
                    status.MessageId, status.Id);
                status.MarkAsPoisoned("InboxMessage record not found — likely deleted.", timeProvider);
                RatatoskrDiagnostics.InboxPoisonCount.Add(1);
                await dbContext.SaveChangesAsync(cancellationToken);
                continue;
            }

            var registration = handlerRegistry.GetByKey(status.HandlerKey);
            if (registration == null)
            {
                logger.LogWarning(
                    "Handler key '{HandlerKey}' is no longer registered. Poisoning status '{StatusId}'.",
                    status.HandlerKey, status.Id);
                status.MarkAsPoisoned(
                    $"Handler key '{status.HandlerKey}' is not registered. The handler may have been removed or renamed.",
                    timeProvider);
                RatatoskrDiagnostics.InboxPoisonCount.Add(1);
                await dbContext.SaveChangesAsync(cancellationToken);
                continue;
            }

            // Deserialize properties before the handler try-catch so observers always fire
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
                RatatoskrDiagnostics.InboxPoisonCount.Add(1);
                await dbContext.SaveChangesAsync(cancellationToken);
                continue;
            }

            Activity? deliverActivity = null;
            Exception? handlerException = null;
            try
            {
                // Start a Consumer span, parented to the original message trace
                ActivityContext.TryParse(props.TraceParent, props.TraceState, out var parentContext);
                deliverActivity = RatatoskrDiagnostics.ActivitySource.StartActivity(
                    "deliver inbox", ActivityKind.Consumer, parentContext);
                if (deliverActivity != null)
                {
                    deliverActivity.SetTag(MessagingSemanticConventions.OperationName, "deliver");
                    deliverActivity.SetTag(MessagingSemanticConventions.OperationType, MessagingSemanticConventions.OperationTypeProcess);
                    deliverActivity.SetTag(MessagingSemanticConventions.System, "ratatoskr");
                    deliverActivity.SetTag(MessagingSemanticConventions.MessageId, props.Id);
                    deliverActivity.SetTag("ratatoskr.inbox.handler.key", status.HandlerKey);
                }

                // Resolve handler in a fresh DI scope (matches MessageDispatcher behaviour)
                using var handlerScope = scopeFactory.CreateScope();
                var handler = handlerScope.ServiceProvider.GetRequiredService(registration.HandlerType);

                // Deserialize message body
                var message = messageSerializer.Deserialize(inboxMessage.Content, registration.MessageType)
                              ?? throw new InvalidOperationException(
                                  $"Deserialized message of type '{registration.MessageType.Name}' was null.");

                // Invoke handler via compiled delegate (no per-call reflection overhead)
                await registration.Invoke(handler, message, props, cancellationToken);

                status.MarkAsCompleted(timeProvider);
                RatatoskrDiagnostics.InboxDeliverCount.Add(1, new TagList { { "status", "success" } });

                logger.LogDebug("Inbox handler '{HandlerKey}' completed for message '{MessageId}'",
                    status.HandlerKey, status.MessageId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Shutdown or lock loss — do NOT count as a handler failure.
                // Leave status as "processing"; stuck detection will recover it on restart.
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
                RatatoskrDiagnostics.InboxDeliverCount.Add(1, new TagList { { "status", "failure" } });
                logger.LogWarning(ex,
                    "Inbox handler '{HandlerKey}' failed for message '{MessageId}', attempt {Attempt}",
                    status.HandlerKey, status.MessageId, status.ErrorCount + 1);
                status.MarkAsFailed(ex.Message, timeProvider, options.MaxRetries, options.MaxRetryDelay);
            }
            finally
            {
                deliverActivity?.Dispose();
            }

            // Persist each handler's state immediately so progress isn't lost on crash
            await dbContext.SaveChangesAsync(CancellationToken.None);

            // Fire InboxDispatched observer — always fires (props always available here)
            await NotifyObserversAsync(new MessageActivity
            {
                Stage = MessageStage.InboxDispatched,
                Properties = props,
                SerializedBody = inboxMessage.Content,
                TransportName = inboxMessage.TransportName,
                IsSuccess = handlerException == null,
                Exception = handlerException,
                Timestamp = timeProvider.GetUtcNow(),
            });

            // Fire InboxPoisoned observer when max retries are exceeded
            if (status.IsPoisoned)
            {
                RatatoskrDiagnostics.InboxPoisonCount.Add(1);
                await NotifyObserversAsync(new MessageActivity
                {
                    Stage = MessageStage.InboxPoisoned,
                    Properties = props,
                    SerializedBody = inboxMessage.Content,
                    TransportName = inboxMessage.TransportName,
                    Exception = handlerException,
                    Timestamp = timeProvider.GetUtcNow(),
                });
            }
        }

        RatatoskrDiagnostics.InboxProcessDuration.Record(Stopwatch.GetElapsedTime(batchStartTimestamp).TotalSeconds);

        return statuses.Length;
    }

    private async Task NotifyObserversAsync(MessageActivity activity)
    {
        foreach (var observer in observers)
        {
            try
            {
                await observer.OnMessageActivity(activity);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Observer failed at {Stage} stage", activity.Stage);
            }
        }
    }
}
