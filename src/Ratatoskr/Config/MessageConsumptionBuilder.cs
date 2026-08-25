using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Ratatoskr.Core;

namespace Ratatoskr.Config;

/// <summary>
/// Fluent builder for registering handlers within a <c>Consumes&lt;T&gt;()</c> call on a consume channel.
/// Handlers with a stable key are inbox-managed; handlers without a key are fire-and-forget.
/// </summary>
public sealed class MessageConsumptionBuilder<TMessage>
    where TMessage : notnull
{
    private readonly IServiceCollection _services;
    internal List<ChannelHandlerRegistration> HandlerRegistrations { get; } = new();

    internal MessageConsumptionBuilder(IServiceCollection services) => _services = services;

    /// <summary>
    /// Registers an inbox handler with a stable key.
    /// Requires the channel to have <c>UseInbox&lt;TDbContext&gt;()</c> configured.
    /// Supports both <see cref="IMessageHandler{TMessage}"/> and <see cref="IBatchMessageHandler{TMessage}"/>.
    /// </summary>
    public MessageConsumptionBuilder<TMessage> WithHandler<THandler>(string stableKey)
        where THandler : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableKey);
        AddHandler<THandler>(isInbox: true, inboxKey: stableKey, legacyKeys: []);
        return this;
    }

    /// <summary>
    /// Registers an inbox handler with a stable key and legacy keys for handler rename transitions.
    /// Legacy keys match existing inbox entries for processing but never create new entries.
    /// Requires the channel to have <c>UseInbox&lt;TDbContext&gt;()</c> configured.
    /// </summary>
    public MessageConsumptionBuilder<TMessage> WithHandler<THandler>(
        string stableKey,
        params string[] legacyKeys
    )
        where THandler : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableKey);
        ArgumentNullException.ThrowIfNull(legacyKeys);
        foreach (var legacyKey in legacyKeys)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(legacyKey, nameof(legacyKeys));
        }
        AddHandler<THandler>(isInbox: true, inboxKey: stableKey, legacyKeys: legacyKeys);
        return this;
    }

    /// <summary>
    /// Registers an inbox batch handler with a stable key and explicit batch settings.
    /// Requires the channel to have <c>UseInbox&lt;TDbContext&gt;()</c> configured.
    /// </summary>
    public MessageConsumptionBuilder<TMessage> WithBatchHandler<THandler>(
        string stableKey,
        int? batchSize = null,
        TimeSpan? batchTimeout = null
    )
        where THandler : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableKey);
        AddHandler<THandler>(
            isInbox: true,
            inboxKey: stableKey,
            legacyKeys: [],
            batchSize: batchSize,
            batchTimeout: batchTimeout
        );
        return this;
    }

    /// <summary>
    /// Registers a fire-and-forget handler (no inbox, no key required).
    /// Only valid on channels without <c>UseInbox&lt;TDbContext&gt;()</c>.
    /// </summary>
    public MessageConsumptionBuilder<TMessage> WithHandler<THandler>()
        where THandler : class
    {
        AddHandler<THandler>(isInbox: false, inboxKey: null, legacyKeys: []);
        return this;
    }

    private void AddHandler<THandler>(
        bool isInbox,
        string? inboxKey,
        IReadOnlyList<string> legacyKeys,
        int? batchSize = null,
        TimeSpan? batchTimeout = null
    )
        where THandler : class
    {
        _services.TryAddScoped<THandler>();

        var isBatch = IsBatchHandlerType(typeof(THandler), typeof(TMessage));

        HandlerRegistrations.Add(
            new ChannelHandlerRegistration
            {
                MessageType = typeof(TMessage),
                HandlerType = typeof(THandler),
                IsInbox = isInbox,
                InboxKey = inboxKey,
                IsBatch = isBatch,
                BatchSize = batchSize,
                BatchTimeout = batchTimeout,
                LegacyKeys = legacyKeys,
            }
        );
    }

    private static bool IsBatchHandlerType(Type handlerType, Type messageType)
    {
        var batchInterfaceDirect = typeof(IBatchMessageHandler<>).MakeGenericType(messageType);
        if (batchInterfaceDirect.IsAssignableFrom(handlerType))
        {
            return true;
        }

        var consumedMessageType = typeof(ConsumedMessage<>).MakeGenericType(messageType);
        var batchInterfaceConsumed = typeof(IBatchMessageHandler<>).MakeGenericType(consumedMessageType);
        return batchInterfaceConsumed.IsAssignableFrom(handlerType);
    }
}
