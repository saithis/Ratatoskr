using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.AsyncApi.Config;
using Ratatoskr.CloudEvents;
using Ratatoskr.Config;
using Ratatoskr.Core;

namespace Ratatoskr;

public sealed class RatatoskrBuilder
{
    public IServiceCollection Services { get; }
    internal CloudEventsOptions CloudEventsOptions { get; } = new();
    internal AsyncApiOptions AsyncApiOptions { get; } = new();
    internal ChannelRegistry ChannelRegistry { get; } = new();

    private readonly List<Action<ChannelRegistry>> _validators = new();
    private readonly List<Action<ChannelRegistry, ChannelHandlerRegistry>> _handlerValidators =
        new();
    private readonly List<Action<IServiceCollection>> _deferredServiceActions = new();

    internal RatatoskrBuilder(IServiceCollection services) => Services = services;

    /// <summary>
    /// Registers a validation callback that runs after all channels are configured.
    /// Used by transport providers to add transport-specific validation rules.
    /// </summary>
    internal void AddValidator(Action<ChannelRegistry> validator) => _validators.Add(validator);

    /// <summary>
    /// Registers a validation callback that runs after channels and handler registry are configured.
    /// Used by infrastructure packages that need to validate handler registrations.
    /// </summary>
    internal void AddHandlerValidator(Action<ChannelRegistry, ChannelHandlerRegistry> validator) =>
        _handlerValidators.Add(validator);

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
        {
            action(Services);
        }
    }

    internal void Validate()
    {
        foreach (var validator in _validators)
        {
            validator(ChannelRegistry);
        }
    }

    internal void ValidateHandlers(ChannelHandlerRegistry handlerRegistry)
    {
        foreach (var validator in _handlerValidators)
        {
            validator(ChannelRegistry, handlerRegistry);
        }
    }

    public RatatoskrBuilder AddEventPublishChannel(
        string channelName,
        Action<PublishChannelBuilder> configure
    )
    {
        ArgumentNullException.ThrowIfNull(configure);
        return AddPublishChannel(channelName, ChannelType.EventPublish, configure);
    }

    public RatatoskrBuilder AddCommandPublishChannel(
        string channelName,
        Action<PublishChannelBuilder> configure
    )
    {
        ArgumentNullException.ThrowIfNull(configure);
        return AddPublishChannel(channelName, ChannelType.CommandPublish, configure);
    }

    public RatatoskrBuilder AddCommandConsumeChannel(
        string channelName,
        Action<ConsumeChannelBuilder> configure
    )
    {
        ArgumentNullException.ThrowIfNull(configure);
        return AddConsumeChannel(channelName, ChannelType.CommandConsume, configure);
    }

    public RatatoskrBuilder AddEventConsumeChannel(
        string channelName,
        Action<ConsumeChannelBuilder> configure
    )
    {
        ArgumentNullException.ThrowIfNull(configure);
        return AddConsumeChannel(channelName, ChannelType.EventConsume, configure);
    }

    private RatatoskrBuilder AddPublishChannel(
        string name,
        ChannelType intent,
        Action<PublishChannelBuilder> configure
    )
    {
        ArgumentNullException.ThrowIfNull(configure);
        var channel = new ChannelRegistration(name, intent);
        var builder = new PublishChannelBuilder(channel);
        configure(builder);
        ChannelRegistry.Register(channel);
        return this;
    }

    private RatatoskrBuilder AddConsumeChannel(
        string name,
        ChannelType intent,
        Action<ConsumeChannelBuilder> configure
    )
    {
        ArgumentNullException.ThrowIfNull(configure);
        var channel = new ChannelRegistration(name, intent);
        var builder = new ConsumeChannelBuilder(channel, Services, this);
        configure(builder);
        ChannelRegistry.Register(channel);
        return this;
    }

    /// <summary>
    /// Configures CloudEvents format options.
    /// </summary>
    public RatatoskrBuilder ConfigureCloudEvents(Action<CloudEventsOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(CloudEventsOptions);
        return this;
    }

    /// <summary>
    /// Configures AsyncAPI document generation options.
    /// </summary>
    public RatatoskrBuilder ConfigureAsyncApi(Action<AsyncApiOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(AsyncApiOptions);
        return this;
    }

    /// <summary>
    /// Configures <see cref="JsonSerializerOptions"/> for the built-in JSON message serializer.
    /// </summary>
    public RatatoskrBuilder ConfigureJsonSerialization(Action<JsonSerializerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(JsonSerializerOptions);
        return this;
    }

    internal JsonSerializerOptions JsonSerializerOptions { get; } = new();
}
