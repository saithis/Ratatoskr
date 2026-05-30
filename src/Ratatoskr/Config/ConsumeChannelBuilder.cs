using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.Core;

namespace Ratatoskr.Config;

/// <summary>
/// Fluent builder for configuring a consume channel and its message handlers.
/// </summary>
public sealed class ConsumeChannelBuilder(
    ChannelRegistration channel,
    IServiceCollection services,
    RatatoskrBuilder ratatoskrBuilder
) : ChannelBuilder(channel)
{
    /// <summary>The service collection for registering DI services from extension methods.</summary>
    internal IServiceCollection Services => services;

    /// <summary>The parent builder for registering validators and deferred actions.</summary>
    internal RatatoskrBuilder RatatoskrBuilder => ratatoskrBuilder;

    /// <summary>
    /// Registers a message type consumed from this channel, with handler registrations.
    /// At least one handler must be registered via <c>WithHandler</c>.
    /// </summary>
    public ConsumeChannelBuilder Consumes<T>(Action<MessageConsumptionBuilder<T>> configure)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(configure);

        AddMessage<T>(configure: null);
        var messageRegistration = Channel.Messages[^1];

        var consumptionBuilder = new MessageConsumptionBuilder<T>(services);
        configure(consumptionBuilder);

        ValidateAndSetHandlers(messageRegistration, consumptionBuilder);

        return this;
    }

    /// <summary>
    /// Registers a message type consumed from this channel, with handler registrations and message configuration.
    /// At least one handler must be registered via <c>WithHandler</c>.
    /// </summary>
    public ConsumeChannelBuilder Consumes<T>(
        Action<MessageConsumptionBuilder<T>> configureHandlers,
        Action<MessageBuilder> configureMessage
    )
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(configureHandlers);
        ArgumentNullException.ThrowIfNull(configureMessage);

        AddMessage<T>(configureMessage);
        var messageRegistration = Channel.Messages[^1];

        var consumptionBuilder = new MessageConsumptionBuilder<T>(services);
        configureHandlers(consumptionBuilder);

        ValidateAndSetHandlers(messageRegistration, consumptionBuilder);

        return this;
    }

    private static void ValidateAndSetHandlers<T>(
        MessageRegistration messageRegistration,
        MessageConsumptionBuilder<T> consumptionBuilder
    )
        where T : notnull
    {
        if (consumptionBuilder.HandlerRegistrations.Count == 0)
        {
            throw new InvalidOperationException(
                $"Consumes<{typeof(T).Name}>() requires at least one handler. "
                    + "Call .WithHandler<THandler>() to register a handler."
            );
        }

        messageRegistration.SetExtension(
            new MessageHandlerRegistrations(consumptionBuilder.HandlerRegistrations)
        );
    }
}

/// <summary>
/// Stores handler registrations on a <see cref="MessageRegistration"/> as an extension.
/// </summary>
internal sealed class MessageHandlerRegistrations(List<ChannelHandlerRegistration> handlers)
{
    public IReadOnlyList<ChannelHandlerRegistration> Handlers => handlers;
}
