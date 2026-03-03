using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ratatoskr.Core;

namespace Ratatoskr.EfCore.Internal;

/// <summary>
/// Single entry point for inbox persistence. Called by <see cref="CompositeInboxRouteInterceptor"/>
/// to persist inbox-managed handler statuses to the database before message dispatch.
/// </summary>
internal class InboxAcceptor<TDbContext>(
    IServiceScopeFactory scopeFactory,
    InboxMessageRegistry inboxMessageRegistry,
    InboxHandlerRegistry inboxHandlerRegistry,
    InboxProcessor<TDbContext> inboxProcessor,
    TimeProvider timeProvider,
    IEnumerable<IMessageActivityObserver> observers,
    ILogger<InboxAcceptor<TDbContext>> logger)
    : IInboxAcceptor where TDbContext : DbContext, IInboxDbContext
{
    public Type DbContextType => typeof(TDbContext);
    public async Task<InboxAcceptOutcome> AcceptAsync(
        byte[] body,
        MessageProperties properties,
        string transportName,
        string channelName,
        CancellationToken cancellationToken)
    {
        // Check if this message type is inbox-managed on this channel
        if (properties.Type == null || !inboxMessageRegistry.IsInboxManaged(channelName, properties.Type))
            return InboxAcceptOutcome.NoHandlers;

        var inboxHandlers = inboxHandlerRegistry.GetByWireTypeName(properties.Type);
        if (inboxHandlers.Count == 0)
            return InboxAcceptOutcome.NoHandlers;

        if (string.IsNullOrWhiteSpace(properties.Id))
        {
            logger.LogError("Cannot persist to inbox: message has no Id. Type: '{Type}'", properties.Type);
            throw new InvalidOperationException("Messages must have a non-empty Id for inbox deduplication.");
        }

        var inboxMessage = InboxMessageEntity.Create(properties.Id, channelName, transportName, body, properties, timeProvider);

        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();
        // Best-effort optimization: skip inserting the message if it already exists.
        // On concurrent delivery the unique constraint is the real dedup mechanism.
        var messageExists = await dbContext.Set<InboxMessageEntity>()
            .AnyAsync(m => m.Id == properties.Id, cancellationToken);

        if (!messageExists)
        {
            dbContext.Set<InboxMessageEntity>().Add(inboxMessage);
            logger.LogDebug("Accepted new inbox message '{MessageId}' of type '{Type}'", properties.Id, properties.Type);
        }
        else
        {
            logger.LogDebug("Inbox message '{MessageId}' already exists (duplicate delivery), updating handler statuses only", properties.Id);
        }

        // Insert InboxHandlerStatuses for handlers not yet present (dedup per handler key).
        // On concurrent delivery, the unique constraint on (MessageId, HandlerKey) prevents duplicates.
        var existingKeys = await dbContext.Set<InboxHandlerStatusEntity>()
            .Where(s => s.MessageId == properties.Id)
            .Select(s => s.HandlerKey)
            .ToHashSetAsync(cancellationToken);

        foreach (var handler in inboxHandlers.Where(h => !existingKeys.Contains(h.Key)))
        {
            dbContext.Set<InboxHandlerStatusEntity>().Add(
                InboxHandlerStatusEntity.Create(properties.Id, handler.Key, timeProvider));
            logger.LogDebug("Created inbox handler status for key '{HandlerKey}' on message '{MessageId}'",
                handler.Key, properties.Id);
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
                properties.Id);
            return InboxAcceptOutcome.Duplicate;
        }

        logger.LogDebug("Persisted inbox entries for message '{MessageId}', {HandlerCount} handler(s)",
            properties.Id, inboxHandlers.Count);

        // Notify observers that the message has been accepted into the inbox
        await observers.NotifyAsync(new MessageActivity
        {
            Stage = MessageStage.InboxQueued,
            Properties = properties,
            SerializedBody = body,
            TransportName = transportName,
            Timestamp = timeProvider.GetUtcNow(),
        }, logger);

        await inboxProcessor.TriggerAsync(cancellationToken);

        return InboxAcceptOutcome.Accepted;
    }
}

/// <summary>
/// Outcome of <see cref="InboxAcceptor{TDbContext}.AcceptAsync"/>.
/// </summary>
internal enum InboxAcceptOutcome
{
    /// <summary>No inbox-managed handlers exist for this message type on this channel.</summary>
    NoHandlers,

    /// <summary>Inbox entries were successfully persisted for the first time.</summary>
    Accepted,

    /// <summary>A concurrent instance already persisted the inbox entries (unique constraint race).</summary>
    Duplicate,
}
