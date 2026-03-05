using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Ratatoskr.Core;
using Ratatoskr.Local;

namespace Ratatoskr.EfCore.Internal;

internal class OutboxTriggerInterceptor<TDbContext>(
    OutboxProcessor<TDbContext> outboxProcessor,
    IMessageSerializer messageSerializer,
    IMessagePropertiesEnricher enricher,
    ChannelRegistry channelRegistry,
    ChannelHandlerRegistry channelHandlerRegistry,
    IEnumerable<IMessageActivityObserver> observers,
    TimeProvider timeProvider,
    ILogger<OutboxTriggerInterceptor<TDbContext>> logger,
    IProcessorTrigger? inboxProcessorTrigger = null)
    : SaveChangesInterceptor where TDbContext : DbContext, IOutboxDbContext
{
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default
    )
    {
        DbContext? context = eventData.Context;
        if (context == null)
        {
            return result;
        }

        if (context is not IOutboxDbContext outboxDbContext)
            throw new InvalidOperationException("Expected IOutboxDbContext");

        while (outboxDbContext.OutboxMessages.Queue.TryPeek(out var item))
        {
            var enrichedProperties = enricher.Enrich(item.Message.GetType(), item.Properties);
            var serializedMessage = messageSerializer.Serialize(item.Message);
            enrichedProperties.ContentType = messageSerializer.ContentType;

            if (enrichedProperties.Transports.Count == 0)
            {
                logger.LogError("No transports found for message '{MessageType}'", item.Message.GetType());
                throw new InvalidOperationException($"No transports found for message '{item.Message.GetType()}'.");
            }

            foreach (var transport in enrichedProperties.Transports)
            {
                var outboxMessage = OutboxMessageEntity.Create(serializedMessage, enrichedProperties, timeProvider, transport);
                context.Set<OutboxMessageEntity>().Add(outboxMessage);
            }

            // For local transport: write inbox entries in the same transaction
            // Only for channels with inbox on the SAME DbContext as this outbox.
            if (enrichedProperties.Transports.Contains(LocalTransportConstants.TransportName)
                && context is IInboxDbContext
                && enrichedProperties.Type != null)
            {
                CreateSameTransactionInboxEntries(context, enrichedProperties, serializedMessage);
            }

            outboxDbContext.OutboxMessages.Queue.TryDequeue(out _);

            await observers.NotifyAsync(new MessageActivity
            {
                Stage = MessageStage.OutboxStaged,
                Properties = enrichedProperties,
                SerializedBody = serializedMessage,
                Message = item.Message,
                MessageType = item.Message.GetType(),
                Timestamp = timeProvider.GetUtcNow(),
            }, logger);
        }

        return result;
    }

    private void CreateSameTransactionInboxEntries(
        DbContext context,
        MessageProperties enrichedProperties,
        byte[] serializedMessage)
    {
        // Find all consume channels for this message wire type
        var consumeChannels = channelRegistry.FindConsumeChannelsForType(enrichedProperties.Type!);
        var inboxEntriesCreated = false;

        foreach (var (channel, _) in consumeChannels)
        {
            var inboxConfig = channel.GetExtension<ChannelInboxConfig>();
            if (inboxConfig == null) continue;

            // Only create same-tx entries if the channel's inbox DbContext matches this outbox's DbContext
            if (inboxConfig.DbContextType != typeof(TDbContext)) continue;

            var inboxHandlers = channelHandlerRegistry.GetInboxHandlers(channel.ChannelName);
            if (inboxHandlers.Count == 0) continue;

            if (string.IsNullOrWhiteSpace(enrichedProperties.Id))
                throw new InvalidOperationException(
                    $"Inbox requires a non-empty message id for '{enrichedProperties.Type}'.");

            if (!inboxEntriesCreated)
            {
                context.Set<InboxMessageEntity>().Add(
                    InboxMessageEntity.Create(enrichedProperties.Id, LocalTransportConstants.TransportName,
                        serializedMessage, enrichedProperties, timeProvider));
                inboxEntriesCreated = true;
            }

            foreach (var handler in inboxHandlers)
            {
                context.Set<InboxHandlerStatusEntity>().Add(
                    InboxHandlerStatusEntity.Create(enrichedProperties.Id, handler.InboxKey!, timeProvider));
            }

            logger.LogDebug(
                "Created inbox entries for local message '{MessageId}' on channel '{Channel}' with {HandlerCount} handler(s) in outbox transaction",
                enrichedProperties.Id, channel.ChannelName, inboxHandlers.Count);
        }
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default
    )
    {
        if (eventData.EntitiesSavedCount == 0)
            return result;

        var outboxMessages = eventData.Context?.ChangeTracker.Entries<OutboxMessageEntity>() ?? [];
        if (outboxMessages.Any(e => e.Entity.ProcessedAt == null))
        {
            await outboxProcessor.TriggerAsync(cancellationToken);
        }

        if (inboxProcessorTrigger != null)
        {
            var inboxStatuses = eventData.Context?.ChangeTracker.Entries<InboxHandlerStatusEntity>() ?? [];
            if (inboxStatuses.Any(e => e.Entity.CompletedAt == null))
            {
                await inboxProcessorTrigger.TriggerAsync(cancellationToken);
            }
        }

        return result;
    }
}
