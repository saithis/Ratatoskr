using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Ratatoskr.Core;

namespace Ratatoskr.EfCore.Internal;

internal class OutboxTriggerInterceptor<TDbContext>(
    OutboxProcessor<TDbContext> outboxProcessor,
    IMessageSerializer messageSerializer,
    IMessagePropertiesEnricher enricher,
    IEnumerable<IMessageActivityObserver> observers,
    TimeProvider timeProvider,
    ILogger<OutboxTriggerInterceptor<TDbContext>> logger)
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

            // Only dequeue after successful serialization
            outboxDbContext.OutboxMessages.Queue.TryDequeue(out _);

            await observers.NotifyAsync(new OutboxMessageStaged
            {
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

        return result;
    }
}
