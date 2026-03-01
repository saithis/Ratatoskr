using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ratatoskr.Core;
using Ratatoskr.Local;

namespace Ratatoskr.EfCore.Internal;

/// <summary>
/// Durable replacement for <see cref="LocalMessageSender"/> when the inbox pattern is configured
/// alongside the local transport.
/// <para>
/// Instead of writing only to the in-memory channel, this sender:
/// <list type="number">
/// <item><description>Persists inbox-managed handler statuses to the database (crash-safe).</description></item>
/// <item><description>Writes to the in-memory channel so <c>LocalTransportConsumer</c> can dispatch non-inbox handlers.</description></item>
/// </list>
/// </para>
/// Crash safety: after <see cref="SendAsync"/> returns, inbox handlers are durably persisted.
/// If the channel message is lost (crash), the <see cref="InboxProcessor{TDbContext}"/> still picks up
/// the handler statuses on restart. If the outbox retries the send, inbox deduplication prevents double-delivery.
/// </summary>
internal class DurableLocalMessageSender<TDbContext>(
    Channel<LocalMessage> messageChannel,
    IServiceScopeFactory scopeFactory,
    InboxHandlerRegistry inboxHandlerRegistry,
    InboxProcessor<TDbContext> inboxProcessor,
    TimeProvider timeProvider,
    IEnumerable<IMessageActivityObserver> observers,
    ILogger<DurableLocalMessageSender<TDbContext>> logger)
    : IMessageSender
    where TDbContext : DbContext, IInboxDbContext
{
    public string TransportName => LocalTransportConstants.TransportName;

    public async Task SendAsync(byte[] content, MessageProperties props, CancellationToken cancellationToken)
    {
        var startTimestamp = Stopwatch.GetTimestamp();

        using var activity = RatatoskrDiagnostics.ActivitySource.StartActivity(
            "send local",
            ActivityKind.Client,
            Activity.Current?.Context ?? default);

        if (activity != null)
        {
            props.TraceParent = activity.Id;
            props.TraceState = activity.TraceStateString;

            activity.SetTag(MessagingSemanticConventions.OperationName, "send");
            activity.SetTag(MessagingSemanticConventions.OperationType, MessagingSemanticConventions.OperationTypeSend);
            activity.SetTag(MessagingSemanticConventions.System, "local");
            activity.SetTag(MessagingSemanticConventions.MessageId, props.Id);
            activity.SetTag(MessagingSemanticConventions.MessageBodySize, content.Length);
        }

        var transportMessage = LocalTransportMessageSnapshotFactory.Create(content, props);
        Exception? sendException = null;

        try
        {
            // Step 1: Write inbox-managed handlers to DB (crash-safe at this point)
            var inboxHandlers = props.Type != null
                ? inboxHandlerRegistry.GetByWireTypeName(props.Type)
                : Array.Empty<InboxHandlerRegistration>();

            if (inboxHandlers.Count > 0)
            {
                await PersistToInboxAsync(content, props, inboxHandlers, cancellationToken);

                // Notify observers that the message has been accepted into the inbox
                foreach (var observer in observers)
                {
                    try
                    {
                        await observer.OnMessageActivity(new MessageActivity
                        {
                            Stage = MessageStage.InboxQueued,
                            Properties = props,
                            SerializedBody = content,
                            TransportName = TransportName,
                            Timestamp = timeProvider.GetUtcNow(),
                        });
                    }
                    catch
                    {
                        // Observer failures must not affect the pipeline
                    }
                }

                await inboxProcessor.TriggerAsync(cancellationToken);
            }

            // Step 2: Write to in-memory channel so LocalTransportConsumer can process non-inbox handlers.
            // For inbox-only messages this is still done so MessageDispatcher runs (it will skip inbox handlers).
            await messageChannel.Writer.WriteAsync(new LocalMessage(content, props), cancellationToken);
        }
        catch (Exception ex)
        {
            sendException = ex;
            activity?.SetTag(MessagingSemanticConventions.ErrorType, ex.GetType().FullName);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
        finally
        {
            var duration = Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds;
            var tags = new TagList
            {
                { MessagingSemanticConventions.System, "local" },
                { MessagingSemanticConventions.OperationName, "send" },
                { MessagingSemanticConventions.OperationType, MessagingSemanticConventions.OperationTypeSend },
            };
            if (sendException != null)
                tags.Add(MessagingSemanticConventions.ErrorType, sendException.GetType().FullName);

            RatatoskrDiagnostics.ClientOperationDuration.Record(duration, tags);
            RatatoskrDiagnostics.ClientSentMessages.Add(1, tags);

            var sentTimestamp = timeProvider.GetUtcNow();
            foreach (var observer in observers)
            {
                try
                {
                    await observer.OnMessageActivity(new MessageActivity
                    {
                        Stage = MessageStage.Sent,
                        Properties = props,
                        SerializedBody = content,
                        TransportName = TransportName,
                        TransportMessage = transportMessage,
                        Exception = sendException,
                        Timestamp = sentTimestamp,
                    });
                }
                catch
                {
                    // Observer failures must not affect the pipeline
                }
            }
        }
    }

    private async Task PersistToInboxAsync(
        byte[] content,
        MessageProperties props,
        IReadOnlyList<InboxHandlerRegistration> inboxHandlers,
        CancellationToken cancellationToken)
    {
        var messageId = props.Id;
        if (string.IsNullOrWhiteSpace(messageId))
        {
            logger.LogError("Cannot persist to inbox: message has no Id. Type: '{Type}'", props.Type);
            throw new InvalidOperationException("Messages must have a non-empty Id for inbox deduplication.");
        }

        InboxMessageEntity.ValidateIdLength(messageId);

        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();

        // Best-effort optimization: skip inserting the message if it already exists.
        // On concurrent delivery the unique constraint is the real dedup mechanism.
        var messageExists = await dbContext.Set<InboxMessageEntity>()
            .AnyAsync(m => m.Id == messageId, cancellationToken);

        if (!messageExists)
        {
            dbContext.Set<InboxMessageEntity>().Add(
                InboxMessageEntity.Create(messageId, LocalTransportConstants.TransportName, content, props, timeProvider));
        }

        // Insert InboxHandlerStatuses for handlers not yet present
        var existingKeys = await dbContext.Set<InboxHandlerStatusEntity>()
            .Where(s => s.MessageId == messageId)
            .Select(s => s.HandlerKey)
            .ToHashSetAsync(cancellationToken);

        foreach (var handler in inboxHandlers.Where(h => !existingKeys.Contains(h.Key)))
        {
            dbContext.Set<InboxHandlerStatusEntity>().Add(
                InboxHandlerStatusEntity.Create(messageId, handler.Key, timeProvider));
        }

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (DbExceptionHelper.IsUniqueConstraintViolation(ex))
        {
            // Another instance or an outbox retry already persisted this message.
            // The unique constraint fired — safe to ignore.
            logger.LogDebug(
                "Inbox entries for message '{MessageId}' were already inserted by a concurrent instance (unique constraint). Ignoring.",
                messageId);
            return;
        }

        logger.LogDebug("Persisted inbox entries for message '{MessageId}', {HandlerCount} handler(s)",
            messageId, inboxHandlers.Count);
    }
}
