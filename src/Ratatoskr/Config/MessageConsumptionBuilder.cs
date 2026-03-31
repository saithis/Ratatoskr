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
        AddHandler<THandler>(isInbox: true, inboxKey: stableKey, fallbackKeys: null);
        return this;
    }

    /// <summary>
    /// Registers an inbox handler with a stable key and fallback keys from previous renames.
    /// Existing inbox entries matching any fallback key will be processed by this handler.
    /// New inbox entries are always created with <paramref name="stableKey"/>, not fallback keys.
    /// </summary>
    /// <param name="stableKey">The current handler key used for new inbox entries.</param>
    /// <param name="fallbackKeys">Previous handler keys to match against for existing inbox entries.</param>
    public MessageConsumptionBuilder<TMessage> WithHandler<THandler>(string stableKey, params string[] fallbackKeys)
        where THandler : class, IMessageHandler<TMessage>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableKey);
        foreach (var key in fallbackKeys)
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
        AddHandler<THandler>(isInbox: true, inboxKey: stableKey, fallbackKeys: fallbackKeys);
        return this;
    }

    /// <summary>
    /// Registers a fire-and-forget handler (no inbox, no key required).
    /// Only valid on channels without <c>UseInbox&lt;TDbContext&gt;()</c>.
    /// </summary>
    public MessageConsumptionBuilder<TMessage> WithHandler<THandler>()
        where THandler : class, IMessageHandler<TMessage>
    {
        AddHandler<THandler>(isInbox: false, inboxKey: null, fallbackKeys: null);
        return this;
    }

    private void AddHandler<THandler>(bool isInbox, string? inboxKey, IReadOnlyList<string>? fallbackKeys)
        where THandler : class, IMessageHandler<TMessage>
    {
        _services.TryAddScoped<THandler>();

        HandlerRegistrations.Add(new ChannelHandlerRegistration(
            typeof(TMessage),
            typeof(THandler),
            isInbox,
            inboxKey,
            fallbackKeys));
    }
}
