using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Ratatoskr.Core;

namespace Ratatoskr.Config;

/// <summary>
/// Fluent builder for registering handlers within a <c>Consumes&lt;T&gt;()</c> call on a consume channel.
/// Handlers with a stable key are inbox-managed; handlers without a key are fire-and-forget.
/// </summary>
public class MessageConsumptionBuilder<TMessage> where TMessage : notnull
{
    private readonly IServiceCollection _services;
    internal List<ChannelHandlerRegistration> HandlerRegistrations { get; } = new();

    internal MessageConsumptionBuilder(IServiceCollection services)
    {
        _services = services;
    }

    /// <summary>
    /// Registers an inbox handler with a stable key.
    /// Requires the channel to have <c>UseInbox&lt;TDbContext&gt;()</c> configured.
    /// </summary>
    public MessageConsumptionBuilder<TMessage> WithHandler<THandler>(string stableKey)
        where THandler : class, IMessageHandler<TMessage>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableKey);
        AddHandler<THandler>(isInbox: true, inboxKey: stableKey);
        return this;
    }

    /// <summary>
    /// Registers an inbox handler with a stable key and additional configuration.
    /// Use <c>h.WithoutInbox()</c> to opt out of inbox management.
    /// </summary>
    public MessageConsumptionBuilder<TMessage> WithHandler<THandler>(string stableKey, Action<HandlerBuilder> configure)
        where THandler : class, IMessageHandler<TMessage>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableKey);

        var registration = new HandlerRegistration();
        var handlerBuilder = new HandlerBuilder(registration);
        configure(handlerBuilder);

        var optOut = registration.GetExtension<DeferredProcessingOverride>()?.OptOut == true;
        AddHandler<THandler>(isInbox: !optOut, inboxKey: optOut ? null : stableKey);
        return this;
    }

    /// <summary>
    /// Registers a fire-and-forget handler (no inbox, no key required).
    /// </summary>
    public MessageConsumptionBuilder<TMessage> WithHandler<THandler>()
        where THandler : class, IMessageHandler<TMessage>
    {
        AddHandler<THandler>(isInbox: false, inboxKey: null);
        return this;
    }

    /// <summary>
    /// Registers a fire-and-forget handler with additional configuration.
    /// </summary>
    public MessageConsumptionBuilder<TMessage> WithHandler<THandler>(Action<HandlerBuilder> configure)
        where THandler : class, IMessageHandler<TMessage>
    {
        var registration = new HandlerRegistration();
        var handlerBuilder = new HandlerBuilder(registration);
        configure(handlerBuilder);

        AddHandler<THandler>(isInbox: false, inboxKey: null);
        return this;
    }

    private void AddHandler<THandler>(bool isInbox, string? inboxKey)
        where THandler : class, IMessageHandler<TMessage>
    {
        _services.TryAddScoped<THandler>();
        _services.AddScoped<IMessageHandler<TMessage>>(sp => sp.GetRequiredService<THandler>());

        HandlerRegistrations.Add(new ChannelHandlerRegistration(
            typeof(TMessage),
            typeof(THandler),
            isInbox,
            inboxKey));
    }
}

/// <summary>
/// Marker extension set on <see cref="HandlerRegistration"/> by <c>WithoutInbox()</c>
/// to explicitly opt this handler out of deferred (inbox) processing.
/// </summary>
internal class DeferredProcessingOverride
{
    internal bool OptOut { get; init; }
}
