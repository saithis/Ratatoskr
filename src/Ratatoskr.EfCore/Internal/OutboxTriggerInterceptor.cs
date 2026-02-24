using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Ratatoskr.Core;

namespace Ratatoskr.EfCore.Internal;

internal class OutboxTriggerInterceptor<TDbContext>(
    OutboxProcessor<TDbContext> outboxProcessor,
    IMessageSerializer messageSerializer,
    IMessagePropertiesEnricher enricher,
    ChannelRegistry channelRegistry,
    IEnumerable<IMessageActivityObserver> observers,
    TimeProvider timeProvider)
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

            // Determine transports for this message's channel
            var publishInfo = channelRegistry.GetPublishInformation(item.Message.GetType());
            var transports = publishInfo?.Channel.Transports;

            if (transports is { Count: > 0 })
            {
                // Create one outbox entity per transport
                foreach (var transport in transports)
                {
                    var outboxMessage = OutboxMessageEntity.Create(serializedMessage, enrichedProperties, timeProvider, transport);
                    context.Set<OutboxMessageEntity>().Add(outboxMessage);
                }
            }
            else
            {
                // Backward compat: no explicit transports, create a single entity with empty transport name
                var outboxMessage = OutboxMessageEntity.Create(serializedMessage, enrichedProperties, timeProvider, string.Empty);
                context.Set<OutboxMessageEntity>().Add(outboxMessage);
            }

            // Only dequeue after successful serialization
            outboxDbContext.OutboxMessages.Queue.TryDequeue(out _);

            foreach (var observer in observers)
            {
                try
                {
                    await observer.OnMessageActivity(new MessageActivity
                    {
                        Stage = MessageStage.OutboxStaged,
                        Properties = enrichedProperties,
                        SerializedBody = serializedMessage,
                        Message = item.Message,
                        MessageType = item.Message.GetType(),
                        Timestamp = timeProvider.GetUtcNow(),
                    });
                }
                catch
                {
                    // Observer failures must not affect the pipeline
                }
            }
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

        return result;
    }
}