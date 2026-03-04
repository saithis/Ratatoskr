using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Ratatoskr.AsyncApi.Config;
using Ratatoskr.CloudEvents;
using Ratatoskr.Config;
using Ratatoskr.Core;

namespace Ratatoskr;

public class RatatoskrBuilder
{
    public IServiceCollection Services { get; }
    internal CloudEventsOptions CloudEventsOptions { get; } = new();
    internal AsyncApiOptions AsyncApiOptions { get; } = new();
    internal ChannelRegistry ChannelRegistry { get; } = new();

    private readonly Dictionary<Type, object> _extensions = new();
    private readonly List<Action<ChannelRegistry>> _validators = new();
    private readonly List<Action<IServiceCollection>> _deferredServiceActions = new();

    /// <summary>
    /// Records of all handlers that may need inbox registration.
    /// Finalized by <see cref="UseEfCoreInbox"/> via a deferred action once the global
    /// inbox configuration is known.
    /// </summary>
    internal List<HandlerRegistration> PendingHandlers { get; } = new();

    internal RatatoskrBuilder(IServiceCollection services)
    {
        Services = services;
    }

    /// <summary>Gets a typed extension object, or null if not set.</summary>
    internal T? GetExtension<T>() where T : class =>
        _extensions.TryGetValue(typeof(T), out var value) ? (T)value : null;

    /// <summary>Sets a typed extension object.</summary>
    internal void SetExtension<T>(T value) where T : class =>
        _extensions[typeof(T)] = value;

    /// <summary>Gets an existing extension or creates and stores one using the factory.</summary>
    internal T GetOrSetExtension<T>(Func<T> factory) where T : class
    {
        if (_extensions.TryGetValue(typeof(T), out var value))
            return (T)value;
        var created = factory();
        _extensions[typeof(T)] = created;
        return created;
    }

    /// <summary>
    /// Registers a validation callback that runs after all channels are configured.
    /// Used by transport providers to add transport-specific validation rules.
    /// </summary>
    internal void AddValidator(Action<ChannelRegistry> validator) => _validators.Add(validator);

    /// <summary>
    /// Queues a service registration action that runs after the full <c>configure</c> callback completes.
    /// This allows transport extensions (e.g. UseEfCoreInbox) to inspect other registrations made
    /// during the same builder call, regardless of their order.
    /// </summary>
    internal void AddDeferredServiceAction(Action<IServiceCollection> action) =>
        _deferredServiceActions.Add(action);

    internal void ExecuteDeferredActions()
    {
        foreach (var action in _deferredServiceActions)
            action(Services);
    }

    internal void Validate()
    {
        foreach (var validator in _validators)
            validator(ChannelRegistry);
    }

    #region New Channel Config

    public RatatoskrBuilder AddEventPublishChannel(string channelName, Action<PublishChannelBuilder> configure)
        => AddPublishChannel(channelName, ChannelType.EventPublish, configure);

    public RatatoskrBuilder AddCommandPublishChannel(string channelName, Action<PublishChannelBuilder> configure)
        => AddPublishChannel(channelName, ChannelType.CommandPublish, configure);

    public RatatoskrBuilder AddCommandConsumeChannel(string channelName, Action<ConsumeChannelBuilder> configure)
        => AddConsumeChannel(channelName, ChannelType.CommandConsume, configure);

    public RatatoskrBuilder AddEventConsumeChannel(string channelName, Action<ConsumeChannelBuilder> configure)
        => AddConsumeChannel(channelName, ChannelType.EventConsume, configure);

    private RatatoskrBuilder AddPublishChannel(string name, ChannelType intent, Action<PublishChannelBuilder> configure)
    {
        var channel = new ChannelRegistration(name, intent);
        var builder = new PublishChannelBuilder(channel);
        configure(builder);
        ChannelRegistry.Register(channel);
        return this;
    }

    private RatatoskrBuilder AddConsumeChannel(string name, ChannelType intent, Action<ConsumeChannelBuilder> configure)
    {
        var channel = new ChannelRegistration(name, intent);
        var builder = new ConsumeChannelBuilder(channel);
        configure(builder);
        ChannelRegistry.Register(channel);
        return this;
    }

    #endregion

    /// <summary>
    /// Configures CloudEvents format options.
    /// </summary>
    public RatatoskrBuilder ConfigureCloudEvents(Action<CloudEventsOptions> configure)
    {
        configure(CloudEventsOptions);
        return this;
    }

    /// <summary>
    /// Configures AsyncAPI document generation options.
    /// </summary>
    public RatatoskrBuilder ConfigureAsyncApi(Action<AsyncApiOptions> configure)
    {
        configure(AsyncApiOptions);
        return this;
    }

    /// <summary>
    /// Registers a message handler with a stable key.
    /// The key is used by the inbox as the deduplication and retry key.
    /// </summary>
    public RatatoskrBuilder AddHandler<TMessage, THandler>(string key, Action<HandlerBuilder>? configure = null)
        where TMessage : notnull
        where THandler : class, IMessageHandler<TMessage>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return AddHandlerCore<TMessage, THandler>(key, configure);
    }

    /// <summary>
    /// Registers a message handler.
    /// Use <paramref name="configure"/> to attach infrastructure-specific options
    /// (e.g. inbox participation via the <c>Ratatoskr.EfCore</c> package).
    /// </summary>
    public RatatoskrBuilder AddHandler<TMessage, THandler>(Action<HandlerBuilder>? configure = null)
        where TMessage : notnull
        where THandler : class, IMessageHandler<TMessage>
    {
        return AddHandlerCore<TMessage, THandler>(key: null, configure);
    }

    private RatatoskrBuilder AddHandlerCore<TMessage, THandler>(string? key, Action<HandlerBuilder>? configure)
        where TMessage : notnull
        where THandler : class, IMessageHandler<TMessage>
    {
        if (PendingHandlers.Any(h => h.HandlerType == typeof(THandler) && h.MessageType == typeof(TMessage)))
            throw new InvalidOperationException(
                $"Handler '{typeof(THandler).Name}' is already registered for message type '{typeof(TMessage).Name}'. " +
                $"Each handler type can only be registered once per message type.");

        Services.AddScoped<THandler>();
        Services.AddScoped<IMessageHandler<TMessage>>(sp => sp.GetRequiredService<THandler>());

        key ??= typeof(THandler).GetCustomAttribute<HandlerKeyAttribute>()?.Key;
        var registration = new HandlerRegistration(typeof(THandler), typeof(TMessage), key);
        if (configure != null)
        {
            var builder = new HandlerBuilder(registration);
            configure(builder);
        }

        PendingHandlers.Add(registration);

        return this;
    }

    /// <summary>
    /// Registers a message handler instance (singleton).
    /// Handler instances are not eligible for inbox management.
    /// </summary>
    public RatatoskrBuilder AddHandler<TMessage, THandler>(THandler handler)
        where TMessage : notnull
        where THandler : class, IMessageHandler<TMessage>
    {
        Services.AddSingleton<THandler>(handler);
        Services.AddSingleton<IMessageHandler<TMessage>>(handler);

        return this;
    }
}
