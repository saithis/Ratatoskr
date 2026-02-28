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
    internal InboxHandlerRegistry InboxHandlerRegistry { get; } = new();

    private readonly List<Action<ChannelRegistry>> _validators = new();

    internal RatatoskrBuilder(IServiceCollection services)
    {
        Services = services;
    }

    /// <summary>
    /// Registers a validation callback that runs after all channels are configured.
    /// Used by transport providers to add transport-specific validation rules.
    /// </summary>
    internal void AddValidator(Action<ChannelRegistry> validator) => _validators.Add(validator);

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
    /// Registers a message handler (fire-and-forget, no inbox retry).
    /// </summary>
    public RatatoskrBuilder AddHandler<TMessage, THandler>()
        where TMessage : notnull
        where THandler : class, IMessageHandler<TMessage>
    {
        Services.AddScoped<THandler>();
        Services.AddScoped<IMessageHandler<TMessage>>(sp => sp.GetRequiredService<THandler>());

        return this;
    }

    /// <summary>
    /// Registers a message handler with a stable inbox key for durable, per-handler retry delivery.
    /// When <c>UseEfCoreInbox</c> is configured, this handler will be managed by the inbox processor
    /// instead of being called synchronously. The key must be stable across deployments as it is
    /// persisted to the database for deduplication and retry tracking.
    /// </summary>
    /// <param name="inboxKey">
    /// Stable string key that uniquely identifies this handler. Used as the deduplication key:
    /// the same handler key will only process each message ID once.
    /// </param>
    public RatatoskrBuilder AddHandler<TMessage, THandler>(string inboxKey)
        where TMessage : notnull
        where THandler : class, IMessageHandler<TMessage>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inboxKey);

        Services.AddScoped<THandler>();
        Services.AddScoped<IMessageHandler<TMessage>>(sp => sp.GetRequiredService<THandler>());

        // TODO: this is not guaranteed to actually be the wire name. It can be overwritten via config.
        var wireTypeName = typeof(TMessage).GetCustomAttribute<RatatoskrMessageAttribute>()?.Type;
        InboxHandlerRegistry.Register(inboxKey, typeof(TMessage), typeof(THandler), wireTypeName);

        return this;
    }

    /// <summary>
    /// Registers a message handler instance.
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
