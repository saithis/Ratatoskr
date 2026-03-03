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

    private readonly List<Action<ChannelRegistry>> _validators = new();
    private readonly List<Action<IServiceCollection>> _deferredServiceActions = new();

    /// <summary>
    /// Records of all handlers that may need inbox registration.
    /// Finalized by <see cref="UseEfCoreInbox"/> via a deferred action once the global
    /// inbox configuration is known.
    /// </summary>
    internal List<PendingHandlerRegistration> PendingHandlers { get; } = new();

    internal RatatoskrBuilder(IServiceCollection services)
    {
        Services = services;
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
    /// Registers a message handler.
    /// Use <paramref name="configure"/> to attach infrastructure-specific options
    /// (e.g. inbox participation via the <c>Ratatoskr.EfCore</c> package).
    /// </summary>
    public RatatoskrBuilder AddHandler<TMessage, THandler>(Action<HandlerBuilder>? configure = null)
        where TMessage : notnull
        where THandler : class, IMessageHandler<TMessage>
    {
        Services.AddScoped<THandler>();
        Services.AddScoped<IMessageHandler<TMessage>>(sp => sp.GetRequiredService<THandler>());

        var registration = new HandlerRegistration();
        if (configure != null)
        {
            var builder = new HandlerBuilder(registration);
            configure(builder);
        }

        PendingHandlers.Add(new PendingHandlerRegistration(typeof(TMessage), typeof(THandler), registration));

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

/// <summary>
/// Holds a pending handler registration that will be finalized by infrastructure packages
/// (e.g. inbox) once the global configuration is known.
/// </summary>
internal record PendingHandlerRegistration(
    Type MessageType,
    Type HandlerType,
    HandlerRegistration Registration);
