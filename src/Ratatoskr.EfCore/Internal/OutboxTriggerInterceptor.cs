using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Ratatoskr.Core;

namespace Ratatoskr.EfCore.Internal;

internal partial class OutboxTriggerInterceptor<TDbContext>(
    OutboxProcessor<TDbContext> outboxProcessor,
    IMessageSerializerResolver serializerResolver,
    IMessagePropertiesEnricher enricher,
    ChannelRegistry channelRegistry,
    ChannelHandlerRegistry channelHandlerRegistry,
    IEnumerable<IMessageActivityObserver> observers,
    OutboxOptionsHolder<TDbContext> optionsHolder,
    TimeProvider timeProvider,
    ILogger<OutboxTriggerInterceptor<TDbContext>> logger,
    IProcessorTrigger? inboxProcessorTrigger = null
) : SaveChangesInterceptor
    where TDbContext : DbContext, IOutboxDbContext
{
    private readonly OutboxOptions _options = optionsHolder.Options;
    private readonly IMessageActivityObserver[] _observers = [.. observers];

    /// <summary>
    /// Per-DbContext state for flags set in SavingChangesAsync and read in SavedChangesAsync.
    /// ConditionalWeakTable ensures no memory leak -- entries are collected when the DbContext is GC'd.
    /// This is safe for a singleton interceptor shared across concurrent SaveChanges calls.
    /// </summary>
    private static readonly ConditionalWeakTable<DbContext, StagedFlags> PerContextFlags = new();

    private sealed class StagedFlags
    {
        public bool OutboxEntitiesStaged { get; set; }
        public bool InboxEntitiesStaged { get; set; }
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default
    )
    {
        var context = eventData.Context;
        if (context == null)
        {
            return result;
        }

        if (context is not IOutboxDbContext outboxDbContext)
        {
            throw new InvalidOperationException("Expected IOutboxDbContext");
        }

        var flags = PerContextFlags.GetOrCreateValue(context);
        flags.OutboxEntitiesStaged = false;
        flags.InboxEntitiesStaged = false;

        var stagedItems = outboxDbContext.OutboxMessages.StagedItems;
        if (stagedItems.Count == 0)
        {
            return result;
        }

        // On retry after a failed SaveChanges, detach entities created by the previous attempt
        // to prevent duplicates. These types are internal — only this interceptor adds them.
        DetachAddedEntities<OutboxMessageEntity>(context);
        DetachAddedEntities<InboxMessageEntity>(context);
        DetachAddedEntities<InboxHandlerStatusEntity>(context);

        foreach (var item in stagedItems)
        {
            await StageItemAsync(context, flags, item);
        }

        return result;
    }

    private async Task StageItemAsync(
        DbContext context,
        StagedFlags flags,
        OutboxStagingCollection.Item item
    )
    {
        var enrichedProperties = enricher.Enrich(item.Message.GetType(), item.Properties);
        var serializer = serializerResolver.GetSerializer(item.Message.GetType());
        var serializedMessage = serializer.Serialize(item.Message);
        enrichedProperties.ContentType = serializer.ContentType;

        if (
            _options.MaxMessageSize.HasValue
            && serializedMessage.Length > _options.MaxMessageSize.Value
        )
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Serialized message of type '{item.Message.GetType().Name}' is {serializedMessage.Length} bytes, which exceeds the configured maximum of {_options.MaxMessageSize.Value} bytes."
                )
            );
        }

        if (enrichedProperties.Transports.Count == 0)
        {
            LogNoTransportsFound(logger, item.Message.GetType());
            throw new InvalidOperationException(
                $"No transports found for message '{item.Message.GetType()}'."
            );
        }

        var skipEfCoreOutbox = false;
        if (
            enrichedProperties.Transports.Contains(EfCoreTransportConstants.TransportName)
            && context is IInboxDbContext
            && enrichedProperties.Type != null
        )
        {
            var (sameDbCreated, hasCrossDbChannels) = CreateSameTransactionInboxEntries(
                context,
                flags,
                enrichedProperties,
                serializedMessage
            );
            skipEfCoreOutbox = sameDbCreated && !hasCrossDbChannels;

            if (sameDbCreated && hasCrossDbChannels)
            {
                LogTargetsBothSameAndCrossDb(logger, enrichedProperties.Id);
            }

            if (sameDbCreated)
            {
                await _observers.NotifyAsync(
                    new MessageActivity
                    {
                        Stage = MessageStage.InboxQueued,
                        Properties = enrichedProperties,
                        SerializedBody = serializedMessage,
                        TransportName = EfCoreTransportConstants.TransportName,
                        Timestamp = timeProvider.GetUtcNow(),
                    },
                    logger
                );
            }
        }

        foreach (var transport in enrichedProperties.Transports)
        {
            if (transport == EfCoreTransportConstants.TransportName && skipEfCoreOutbox)
            {
                continue;
            }

            var outboxMessage = OutboxMessageEntity.Create(
                serializedMessage,
                enrichedProperties,
                timeProvider,
                transport
            );
            context.Set<OutboxMessageEntity>().Add(outboxMessage);
            flags.OutboxEntitiesStaged = true;
        }

        await _observers.NotifyAsync(
            new MessageActivity
            {
                Stage = MessageStage.OutboxStaged,
                Properties = enrichedProperties,
                SerializedBody = serializedMessage,
                Message = item.Message,
                MessageType = item.Message.GetType(),
                Timestamp = timeProvider.GetUtcNow(),
            },
            logger
        );
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
        byte[] serializedMessage
    )
    {
        // Find all consume channels for this message wire type
        var consumeChannels = channelRegistry.FindConsumeChannelsForType(enrichedProperties.Type!);
        var inboxEntriesCreated = false;
        var hasCrossDbChannels = false;

        foreach (var (channel, _) in consumeChannels)
        {
            var inboxConfig = channel.GetExtension<ChannelInboxConfig>();
            if (inboxConfig == null)
            {
                continue;
            }

            // Track cross-DbContext channels — they still need an outbox entry
            if (inboxConfig.DbContextType != typeof(TDbContext))
            {
                hasCrossDbChannels = true;
                continue;
            }

            var msgReg = channel.GetMessage(enrichedProperties.Type!);
            if (msgReg == null)
            {
                continue;
            }

            var inboxHandlers = channelHandlerRegistry.GetInboxHandlers(
                channel.ChannelName,
                msgReg.MessageType
            );
            if (inboxHandlers.Count == 0)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(enrichedProperties.Id))
            {
                throw new InvalidOperationException(
                    $"Inbox requires a non-empty message id for '{enrichedProperties.Type}'."
                );
            }

            if (!inboxEntriesCreated)
            {
                context
                    .Set<InboxMessageEntity>()
                    .Add(
                        InboxMessageEntity.Create(
                            enrichedProperties.Id,
                            EfCoreTransportConstants.TransportName,
                            serializedMessage,
                            enrichedProperties,
                            timeProvider
                        )
                    );
                inboxEntriesCreated = true;
                flags.InboxEntitiesStaged = true;
            }

            foreach (var handler in inboxHandlers)
            {
                context
                    .Set<InboxHandlerStatusEntity>()
                    .Add(
                        InboxHandlerStatusEntity.Create(
                            enrichedProperties.Id,
                            handler.InboxKey!,
                            timeProvider
                        )
                    );
            }

            LogCreatedInboxEntries(
                logger,
                enrichedProperties.Id,
                channel.ChannelName,
                inboxHandlers.Count
            );
        }

        return (inboxEntriesCreated, hasCrossDbChannels);
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default
    )
    {
        // Commit: clear staged items now that the transaction succeeded
        if (eventData.Context is IOutboxDbContext outboxDbContext)
        {
            outboxDbContext.OutboxMessages.ClearStaged();
        }

        if (eventData.EntitiesSavedCount == 0)
        {
            return result;
        }

        if (
            eventData.Context != null
            && PerContextFlags.TryGetValue(eventData.Context, out var flags)
        )
        {
            if (flags.OutboxEntitiesStaged)
            {
                await outboxProcessor.TriggerAsync(cancellationToken);
            }

            if (flags.InboxEntitiesStaged && inboxProcessorTrigger != null)
            {
                await inboxProcessorTrigger.TriggerAsync(cancellationToken);
            }

            PerContextFlags.Remove(eventData.Context);
        }

        return result;
    }

    private static void DetachAddedEntities<TEntity>(DbContext context)
        where TEntity : class
    {
        foreach (var entry in context.ChangeTracker.Entries<TEntity>().ToList())
        {
            if (entry.State == EntityState.Added)
            {
                entry.State = EntityState.Detached;
            }
        }
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Error,
        Message = "No transports found for message '{MessageType}'"
    )]
    private static partial void LogNoTransportsFound(ILogger logger, Type messageType);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "Message '{MessageId}' targets both same-DbContext and cross-DbContext inbox channels. Same-DbContext entries were created in this transaction; an outbox entry will also be created for cross-DbContext delivery. The inbox acceptor will deduplicate on delivery."
    )]
    private static partial void LogTargetsBothSameAndCrossDb(ILogger logger, string? messageId);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Debug,
        Message = "Created inbox entries for message '{MessageId}' on channel '{Channel}' with {HandlerCount} handler(s) in outbox transaction"
    )]
    private static partial void LogCreatedInboxEntries(
        ILogger logger,
        string? messageId,
        string channel,
        int handlerCount
    );
}
