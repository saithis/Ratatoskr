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
        CancellationToken cancellationToken)
    {
        var messageId = properties.Id;
        if (string.IsNullOrWhiteSpace(messageId))
        {
            logger.LogError("Cannot accept message into inbox: message has no Id. Type: '{Type}'", properties.Type);
            throw new InvalidOperationException("Messages must have a non-empty Id for inbox deduplication.");
        }

        var transportName = properties.TransportName() ?? "unknown";

        var dbContext = scopedServices.GetRequiredService<TDbContext>();

        // Insert InboxMessage (skip if already exists — dedup)
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

        // Insert InboxHandlerStatuses for handlers not yet present (dedup per handler key)
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

        await dbContext.SaveChangesAsync(cancellationToken);
        await inboxProcessor.TriggerAsync(cancellationToken);
    }
}

/// <summary>Extension to read the transport name from properties metadata.</summary>
file static class MessagePropertiesExtensions
{
    internal static string? TransportName(this MessageProperties properties) =>
        properties.TransportMetadata.TryGetValue("transport-name", out var name) ? name : null;
}
