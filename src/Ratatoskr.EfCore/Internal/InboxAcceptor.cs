using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ratatoskr.Core;

namespace Ratatoskr.EfCore.Internal;

/// <summary>
/// Single entry point for inbox persistence. Called by <see cref="InboxRouteInterceptor{TDbContext}"/>
/// to persist inbox-managed handler statuses to the database before message dispatch.
/// Only processes channels whose inbox DbContext matches <typeparamref name="TDbContext"/>.
/// </summary>
internal class InboxAcceptor<TDbContext>(
    IServiceScopeFactory scopeFactory,
    ChannelRegistry channelRegistry,
    ChannelHandlerRegistry channelHandlerRegistry,
    InboxProcessor<TDbContext> inboxProcessor,
    TimeProvider timeProvider,
    IEnumerable<IMessageActivityObserver> observers,
    ILogger<InboxAcceptor<TDbContext>> logger)
    : IEfCoreInboxAcceptor
    where TDbContext : DbContext, IInboxDbContext
{
    public Type DbContextType => typeof(TDbContext);

    public async Task<InboxAcceptOutcome> AcceptAsync(
        byte[] body,
        MessageProperties properties,
        string transportName,
        string channelName,
        CancellationToken cancellationToken)
    {
        // Check if this channel's inbox DbContext matches our TDbContext
        var channel = channelRegistry.GetConsumeChannel(channelName);
        if (channel == null)
            return InboxAcceptOutcome.NoHandlers;

        var inboxConfig = channel.GetExtension<ChannelInboxConfig>();
        if (inboxConfig == null || inboxConfig.DbContextType != typeof(TDbContext))
            return InboxAcceptOutcome.NoHandlers;

        // Resolve message CLR type from wire type name
        if (properties.Type == null)
            return InboxAcceptOutcome.NoHandlers;

        var msgReg = channel.Messages.FirstOrDefault(m => m.MessageTypeName == properties.Type);
        if (msgReg == null)
            return InboxAcceptOutcome.NoHandlers;

        var inboxHandlers = channelHandlerRegistry.GetInboxHandlers(channelName, msgReg.MessageType);
        if (inboxHandlers.Count == 0)
            return InboxAcceptOutcome.NoHandlers;

        if (string.IsNullOrWhiteSpace(properties.Id))
        {
            logger.LogError("Cannot persist to inbox: message has no Id. Type: '{Type}'", properties.Type);
            throw new InvalidOperationException("Messages must have a non-empty Id for inbox deduplication.");
        }

        var inboxMessage = InboxMessageEntity.Create(properties.Id, transportName, body, properties, timeProvider);

        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();

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

        var existingKeys = await dbContext.Set<InboxHandlerStatusEntity>()
            .Where(s => s.MessageId == properties.Id)
            .Select(s => s.HandlerKey)
            .ToHashSetAsync(cancellationToken);

        foreach (var handler in inboxHandlers.Where(h => !existingKeys.Contains(h.InboxKey!)))
        {
            dbContext.Set<InboxHandlerStatusEntity>().Add(
                InboxHandlerStatusEntity.Create(properties.Id, handler.InboxKey!, timeProvider));
            logger.LogDebug("Created inbox handler status for key '{HandlerKey}' on message '{MessageId}'",
                handler.InboxKey, properties.Id);
        }

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (DbExceptionHelper.IsUniqueConstraintViolation(ex))
        {
            dbContext.ChangeTracker.Clear();
            logger.LogDebug(
                "Inbox entries for message '{MessageId}' were already inserted by a concurrent instance (unique constraint). Ignoring.",
                properties.Id);
            return InboxAcceptOutcome.Duplicate;
        }

        logger.LogDebug("Persisted inbox entries for message '{MessageId}', {HandlerCount} handler(s)",
            properties.Id, inboxHandlers.Count);

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
