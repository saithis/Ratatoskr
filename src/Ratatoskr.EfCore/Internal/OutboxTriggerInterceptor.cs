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
    IEnumerable<IMessageActivityObserver> observers,
    TimeProvider timeProvider,
    ILogger<OutboxTriggerInterceptor<TDbContext>> logger,
    InboxMessageRegistry? inboxMessageRegistry = null,
    InboxHandlerRegistry? inboxHandlerRegistry = null,
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

        // Peek and process items - if serialization fails,
        // successfully processed items are already removed, failed items remain
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

            // Create one outbox entity per transport
            foreach (var transport in enrichedProperties.Transports)
            {
                var outboxMessage = OutboxMessageEntity.Create(serializedMessage, enrichedProperties, timeProvider, transport);
                context.Set<OutboxMessageEntity>().Add(outboxMessage);
            }

            // For local transport messages: write inbox entries in the same transaction
            // so that inbox-managed handlers are guaranteed to be persisted when the
            // outbox entry is created. This replaces the DurableLocalMessageSender approach.
            if (enrichedProperties.Transports.Contains(LocalTransportConstants.TransportName)
                && context is IInboxDbContext
                && inboxMessageRegistry is { IsEmpty: false }
                && inboxHandlerRegistry is { IsEmpty: false }
                && enrichedProperties.Type != null)
            {
                // Find consume channels that handle this message type and have inbox enabled
                foreach (var (channel, _) in channelRegistry.FindConsumeChannelsForType(enrichedProperties.Type))
                {
                    if (!inboxMessageRegistry.IsInboxManaged(channel.ChannelName, enrichedProperties.Type))
                        continue;

                    var inboxHandlers = inboxHandlerRegistry.GetByWireTypeName(enrichedProperties.Type);
                    if (inboxHandlers.Count == 0)
                        continue;

                    if (string.IsNullOrWhiteSpace(enrichedProperties.Id))
                        throw new InvalidOperationException($"Inbox requires a non-empty message id for '{item.Message.GetType().FullName}'.");

                    context.Set<InboxMessageEntity>().Add(
                        InboxMessageEntity.Create(enrichedProperties.Id, channel.ChannelName,
                            LocalTransportConstants.TransportName, serializedMessage, enrichedProperties, timeProvider));

                    foreach (var handler in inboxHandlers)
                    {
                        context.Set<InboxHandlerStatusEntity>().Add(
                            InboxHandlerStatusEntity.Create(enrichedProperties.Id, handler.Key, timeProvider));
                    }

                    logger.LogDebug(
                        "Created inbox entries for local message '{MessageId}' on channel '{ChannelName}' with {HandlerCount} handler(s) in outbox transaction",
                        enrichedProperties.Id, channel.ChannelName, inboxHandlers.Count);
                }
            }

            // Only dequeue after successful serialization
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

        // Trigger inbox processor if inbox handler statuses were created in this transaction
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
