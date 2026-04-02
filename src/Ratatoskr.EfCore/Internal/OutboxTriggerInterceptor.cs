using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Ratatoskr.Core;

namespace Ratatoskr.EfCore.Internal;

internal class OutboxTriggerInterceptor<TDbContext>(
    OutboxProcessor<TDbContext> outboxProcessor,
    IMessageSerializer messageSerializer,
    IMessagePropertiesEnricher enricher,
    ChannelRegistry channelRegistry,
    ChannelHandlerRegistry channelHandlerRegistry,
    IEnumerable<IMessageActivityObserver> observers,
    OutboxOptionsHolder<TDbContext> optionsHolder,
    TimeProvider timeProvider,
    ILogger<OutboxTriggerInterceptor<TDbContext>> logger,
    IProcessorTrigger? inboxProcessorTrigger = null)
    : SaveChangesInterceptor where TDbContext : DbContext, IOutboxDbContext
{
    private readonly OutboxOptions _options = optionsHolder.Options;
    private readonly IMessageActivityObserver[] _observers = observers.ToArray();

    // Per-DbContext state for flags set in SavingChangesAsync and read in SavedChangesAsync.
    // ConditionalWeakTable ensures no memory leak — entries are collected when the DbContext is GC'd.
    // This is safe for a singleton interceptor shared across concurrent SaveChanges calls.
    private static readonly ConditionalWeakTable<DbContext, StagedFlags> _perContextFlags = new();

    private sealed class StagedFlags
    {
        public bool OutboxEntitiesStaged;
        public bool InboxEntitiesStaged;
        public List<OutboxStagingCollection.Item>? DequeuedItems;
        public List<object>? AddedEntities;
    }

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

        var flags = _perContextFlags.GetOrCreateValue(context);
        flags.OutboxEntitiesStaged = false;
        flags.InboxEntitiesStaged = false;
        flags.DequeuedItems ??= new List<OutboxStagingCollection.Item>();
        flags.AddedEntities ??= new List<object>();

        // Clear in case this is a retry and previous state wasn't cleaned up (e.g. unexpected flow)
        flags.DequeuedItems.Clear();
        flags.AddedEntities.Clear();

        while (outboxDbContext.OutboxMessages.Queue.TryPeek(out var item))
        {
            var enrichedProperties = enricher.Enrich(item.Message.GetType(), item.Properties);
            var serializedMessage = messageSerializer.Serialize(item.Message);
            enrichedProperties.ContentType = messageSerializer.ContentType;

            if (_options.MaxMessageSize.HasValue && serializedMessage.Length > _options.MaxMessageSize.Value)
            {
                throw new InvalidOperationException(
                    $"Serialized message of type '{item.Message.GetType().Name}' is {serializedMessage.Length} bytes, " +
                    $"which exceeds the configured maximum of {_options.MaxMessageSize.Value} bytes.");
            }

            if (enrichedProperties.Transports.Count == 0)
            {
                logger.LogError("No transports found for message '{MessageType}'", item.Message.GetType());
                throw new InvalidOperationException($"No transports found for message '{item.Message.GetType()}'.");
            }

            // For EF Core transport with same-DbContext inbox: write inbox entries directly
            // in this transaction and skip the outbox entry (no need to round-trip through OutboxProcessor).
            // An outbox entry is still needed if there are cross-DbContext channels.
            var skipEfCoreOutbox = false;
            if (enrichedProperties.Transports.Contains(EfCoreTransportConstants.TransportName)
                && context is IInboxDbContext
                && enrichedProperties.Type != null)
            {
                var (sameDbCreated, hasCrossDbChannels) = CreateSameTransactionInboxEntries(
                    context, flags, enrichedProperties, serializedMessage);
                skipEfCoreOutbox = sameDbCreated && !hasCrossDbChannels;

                if (sameDbCreated && hasCrossDbChannels)
                {
                    logger.LogWarning(
                        "Message '{MessageId}' targets both same-DbContext and cross-DbContext inbox channels. " +
                        "Same-DbContext entries were created in this transaction; an outbox entry will also be created " +
                        "for cross-DbContext delivery. The inbox acceptor will deduplicate on delivery.",
                        enrichedProperties.Id);
                }

                if (sameDbCreated)
                {
                    await _observers.NotifyAsync(new MessageActivity
                    {
                        Stage = MessageStage.InboxQueued,
                        Properties = enrichedProperties,
                        SerializedBody = serializedMessage,
                        TransportName = EfCoreTransportConstants.TransportName,
                        Timestamp = timeProvider.GetUtcNow(),
                    }, logger);
                }
            }

            foreach (var transport in enrichedProperties.Transports)
            {
                // Skip outbox entry for EF Core transport when ALL inbox channels are
                // same-DbContext (entries already created in this transaction).
                if (transport == EfCoreTransportConstants.TransportName && skipEfCoreOutbox)
                    continue;

                var outboxMessage = OutboxMessageEntity.Create(serializedMessage, enrichedProperties, timeProvider, transport);
                context.Set<OutboxMessageEntity>().Add(outboxMessage);
                flags.AddedEntities.Add(outboxMessage);
                flags.OutboxEntitiesStaged = true;
            }

            outboxDbContext.OutboxMessages.Queue.TryDequeue(out var dequeuedItem);
            if (dequeuedItem != null)
            {
                flags.DequeuedItems.Add(dequeuedItem);
            }

            await _observers.NotifyAsync(new MessageActivity
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

    /// <summary>
    /// Creates inbox entries in the same transaction as the outbox for channels whose inbox
    /// DbContext matches this outbox's DbContext.
    /// Returns (sameDbCreated, hasCrossDbChannels):
    ///   - sameDbCreated: true if any same-DbContext inbox entries were created
    ///   - hasCrossDbChannels: true if any consume channels target a different DbContext
    /// </summary>
    private (bool SameDbCreated, bool HasCrossDbChannels) CreateSameTransactionInboxEntries(
        DbContext context,
        StagedFlags flags,
        MessageProperties enrichedProperties,
        byte[] serializedMessage)
    {
        // Find all consume channels for this message wire type
        var consumeChannels = channelRegistry.FindConsumeChannelsForType(enrichedProperties.Type!);
        var inboxEntriesCreated = false;
        var hasCrossDbChannels = false;

        foreach (var (channel, _) in consumeChannels)
        {
            var inboxConfig = channel.GetExtension<ChannelInboxConfig>();
            if (inboxConfig == null) continue;

            // Track cross-DbContext channels — they still need an outbox entry
            if (inboxConfig.DbContextType != typeof(TDbContext))
            {
                hasCrossDbChannels = true;
                continue;
            }

            var msgReg = channel.GetMessage(enrichedProperties.Type!);
            if (msgReg == null) continue;

            var inboxHandlers = channelHandlerRegistry.GetInboxHandlers(channel.ChannelName, msgReg.MessageType);
            if (inboxHandlers.Count == 0) continue;

            if (string.IsNullOrWhiteSpace(enrichedProperties.Id))
                throw new InvalidOperationException(
                    $"Inbox requires a non-empty message id for '{enrichedProperties.Type}'.");

            if (!inboxEntriesCreated)
            {
                var inboxEntity = InboxMessageEntity.Create(enrichedProperties.Id, EfCoreTransportConstants.TransportName,
                    serializedMessage, enrichedProperties, timeProvider);
                context.Set<InboxMessageEntity>().Add(inboxEntity);
                flags.AddedEntities!.Add(inboxEntity);
                inboxEntriesCreated = true;
                flags.InboxEntitiesStaged = true;
            }

            foreach (var handler in inboxHandlers)
            {
                var statusEntity = InboxHandlerStatusEntity.Create(enrichedProperties.Id, handler.InboxKey!, timeProvider);
                context.Set<InboxHandlerStatusEntity>().Add(statusEntity);
                flags.AddedEntities!.Add(statusEntity);
            }

            logger.LogDebug(
                "Created inbox entries for message '{MessageId}' on channel '{Channel}' with {HandlerCount} handler(s) in outbox transaction",
                enrichedProperties.Id, channel.ChannelName, inboxHandlers.Count);
        }

        return (inboxEntriesCreated, hasCrossDbChannels);
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default
    )
    {
        if (eventData.EntitiesSavedCount == 0)
            return result;

        if (eventData.Context != null && _perContextFlags.TryGetValue(eventData.Context, out var flags))
        {
            if (flags.OutboxEntitiesStaged)
            {
                await outboxProcessor.TriggerAsync(cancellationToken);
            }

            if (flags.InboxEntitiesStaged && inboxProcessorTrigger != null)
            {
                await inboxProcessorTrigger.TriggerAsync(cancellationToken);
            }

            _perContextFlags.Remove(eventData.Context);
        }

        return result;
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        HandleSaveFailure(eventData.Context);
        base.SaveChangesFailed(eventData);
    }

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        HandleSaveFailure(eventData.Context);
        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    public override void SaveChangesCanceled(DbContextEventData eventData)
    {
        HandleSaveFailure(eventData.Context);
        base.SaveChangesCanceled(eventData);
    }

    public override Task SaveChangesCanceledAsync(DbContextEventData eventData, CancellationToken cancellationToken = default)
    {
        HandleSaveFailure(eventData.Context);
        return base.SaveChangesCanceledAsync(eventData, cancellationToken);
    }

    private void HandleSaveFailure(DbContext? context)
    {
        if (context is not IOutboxDbContext outboxDbContext) return;

        if (_perContextFlags.TryGetValue(context, out var flags))
        {
            if (flags.DequeuedItems != null)
            {
                foreach (var item in flags.DequeuedItems)
                {
                    outboxDbContext.OutboxMessages.Queue.Enqueue(item);
                }
                flags.DequeuedItems.Clear();
            }

            if (flags.AddedEntities != null)
            {
                foreach (var entity in flags.AddedEntities)
                {
                    var entry = context.Entry(entity);
                    if (entry.State == EntityState.Added)
                    {
                        entry.State = EntityState.Detached;
                    }
                }
                flags.AddedEntities.Clear();
            }

            flags.OutboxEntitiesStaged = false;
            flags.InboxEntitiesStaged = false;
        }
    }
}
