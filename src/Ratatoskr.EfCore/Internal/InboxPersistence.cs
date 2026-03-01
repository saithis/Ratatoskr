using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ratatoskr.Core;

namespace Ratatoskr.EfCore.Internal;

/// <summary>
/// Shared inbox persistence logic used by both <see cref="DurableLocalMessageSender{TDbContext}"/>
/// and <see cref="InboxInterceptor{TDbContext}"/>. Extracts the duplicated message/handler-status
/// upsert logic, observer notification, and processing trigger into a single place.
/// </summary>
internal static class InboxPersistence
{
    /// <summary>
    /// Persists an inbox message and its handler statuses to the database.
    /// On success, notifies observers with <see cref="MessageStage.InboxQueued"/> and triggers
    /// background processing via <paramref name="triggerProcessing"/>.
    /// Uses unique constraints as the real deduplication mechanism; the best-effort
    /// AnyAsync check is an optimization to avoid unnecessary inserts.
    /// </summary>
    /// <returns>True if new rows were inserted; false if a concurrent instance already persisted them.</returns>
    public static async Task<bool> PersistAsync<TDbContext>(
        IServiceScopeFactory scopeFactory,
        string messageId,
        string transportName,
        byte[] body,
        MessageProperties properties,
        IReadOnlyList<InboxHandlerRegistration> handlers,
        TimeProvider timeProvider,
        IEnumerable<IMessageActivityObserver> observers,
        Func<CancellationToken, ValueTask> triggerProcessing,
        ILogger logger,
        CancellationToken cancellationToken)
        where TDbContext : DbContext, IInboxDbContext
    {
        if (string.IsNullOrWhiteSpace(messageId))
        {
            logger.LogError("Cannot persist to inbox: message has no Id. Type: '{Type}'", properties.Type);
            throw new InvalidOperationException("Messages must have a non-empty Id for inbox deduplication.");
        }
        
        var inboxMessage = InboxMessageEntity.Create(messageId, transportName, body, properties, timeProvider);

        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();
        // Best-effort optimization: skip inserting the message if it already exists.
        // On concurrent delivery the unique constraint is the real dedup mechanism.
        var messageExists = await dbContext.Set<InboxMessageEntity>()
            .AnyAsync(m => m.Id == messageId, cancellationToken);

        if (!messageExists)
        {
            dbContext.Set<InboxMessageEntity>().Add(inboxMessage);
            logger.LogDebug("Accepted new inbox message '{MessageId}' of type '{Type}'", messageId, properties.Type);
        }
        else
        {
            logger.LogDebug("Inbox message '{MessageId}' already exists (duplicate delivery), updating handler statuses only", messageId);
        }

        // Insert InboxHandlerStatuses for handlers not yet present (dedup per handler key).
        // On concurrent delivery, the unique constraint on (MessageId, HandlerKey) prevents duplicates.
        var existingKeys = await dbContext.Set<InboxHandlerStatusEntity>()
            .Where(s => s.MessageId == messageId)
            .Select(s => s.HandlerKey)
            .ToHashSetAsync(cancellationToken);

        foreach (var handler in handlers.Where(h => !existingKeys.Contains(h.Key)))
        {
            dbContext.Set<InboxHandlerStatusEntity>().Add(
                InboxHandlerStatusEntity.Create(messageId, handler.Key, timeProvider));
            logger.LogDebug("Created inbox handler status for key '{HandlerKey}' on message '{MessageId}'",
                handler.Key, messageId);
        }

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (DbExceptionHelper.IsUniqueConstraintViolation(ex))
        {
            // Another instance raced us — the deduplication constraint fired.
            // This is expected and safe: the other instance will process the handlers.
            // Clear the change tracker to avoid corrupting the DbContext state for any
            // subsequent operations in the same scope.
            dbContext.ChangeTracker.Clear();
            logger.LogDebug(
                "Inbox entries for message '{MessageId}' were already inserted by a concurrent instance (unique constraint). Ignoring.",
                messageId);
            return false;
        }

        logger.LogDebug("Persisted inbox entries for message '{MessageId}', {HandlerCount} handler(s)",
            messageId, handlers.Count);

        // Notify observers that the message has been accepted into the inbox
        foreach (var observer in observers)
        {
            try
            {
                await observer.OnMessageActivity(new MessageActivity
                {
                    Stage = MessageStage.InboxQueued,
                    Properties = properties,
                    SerializedBody = body,
                    TransportName = transportName,
                    Timestamp = timeProvider.GetUtcNow(),
                });
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Observer failed at {Stage} stage", MessageStage.InboxQueued);
            }
        }

        await triggerProcessing(cancellationToken);

        return true;
    }
}
