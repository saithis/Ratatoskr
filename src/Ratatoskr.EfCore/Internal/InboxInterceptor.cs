using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ratatoskr.Core;

namespace Ratatoskr.EfCore.Internal;

/// <summary>
/// Called by <see cref="MessageDispatcher"/> when a message arrives on a non-local transport
/// (e.g. RabbitMQ) and inbox-managed handlers are registered for the message type.
/// Persists the message and handler statuses to the database so that <see cref="InboxProcessor{TDbContext}"/>
/// can deliver them with per-handler retry and deduplication.
/// </summary>
internal class InboxInterceptor<TDbContext>(
    InboxProcessor<TDbContext> inboxProcessor,
    TimeProvider timeProvider,
    ILogger<InboxInterceptor<TDbContext>> logger)
    : IInboxInterceptor
    where TDbContext : DbContext, IInboxDbContext
{
    public async Task AcceptAsync(
        IServiceProvider scopedServices,
        byte[] body,
        MessageProperties properties,
        IReadOnlyList<InboxHandlerRegistration> managedHandlers,
        string transportName,
        CancellationToken cancellationToken)
    {
        var messageId = properties.Id;
        if (string.IsNullOrWhiteSpace(messageId))
        {
            logger.LogError("Cannot accept message into inbox: message has no Id. Type: '{Type}'", properties.Type);
            throw new InvalidOperationException("Messages must have a non-empty Id for inbox deduplication.");
        }

        var dbContext = scopedServices.GetRequiredService<TDbContext>();

        // Insert InboxMessage if not already present — dedup via unique constraint on Id.
        // On concurrent delivery (multiple app instances), the second insert is silently ignored.
        var messageExists = await dbContext.Set<InboxMessageEntity>()
            .AnyAsync(m => m.Id == messageId, cancellationToken);

        if (!messageExists)
        {
            dbContext.Set<InboxMessageEntity>().Add(
                InboxMessageEntity.Create(messageId, transportName, body, properties, timeProvider));
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

        foreach (var handler in managedHandlers.Where(h => !existingKeys.Contains(h.Key)))
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
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            // Another instance raced us — the deduplication constraint fired.
            // This is expected and safe: the other instance will process the handlers.
            logger.LogDebug(
                "Inbox message '{MessageId}' was already inserted by a concurrent instance (unique constraint). Ignoring.",
                messageId);
            return;
        }

        await inboxProcessor.TriggerAsync(cancellationToken);
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        // PostgreSQL error code 23505 = unique_violation
        // SQLite error code 19 = SQLITE_CONSTRAINT (unique)
        // SQL Server error code 2601 / 2627 = unique constraint
        var inner = ex.InnerException;
        if (inner == null) return false;
        var msg = inner.Message;
        return msg.Contains("23505")       // PostgreSQL
            || msg.Contains("UNIQUE")      // SQLite
            || msg.Contains("unique")      // SQLite / generic
            || msg.Contains("2601")        // SQL Server
            || msg.Contains("2627");       // SQL Server
    }
}
