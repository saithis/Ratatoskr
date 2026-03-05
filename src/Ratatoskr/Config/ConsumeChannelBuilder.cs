using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.Core;

namespace Ratatoskr.Config;

public class ConsumeChannelBuilder(ChannelRegistration channel, IServiceCollection services, RatatoskrBuilder ratatoskrBuilder) : ChannelBuilder(channel)
{
    /// <summary>The underlying channel registration.</summary>
    internal ChannelRegistration Channel => channel;

    /// <summary>The service collection for registering DI services from extension methods.</summary>
    internal IServiceCollection Services => services;

    /// <summary>The parent builder for registering validators and deferred actions.</summary>
    internal RatatoskrBuilder RatatoskrBuilder => ratatoskrBuilder;

    /// <summary>
    /// Registers a message type consumed from this channel, with handler registrations.
    /// At least one handler must be registered via <c>WithHandler</c>.
    /// </summary>
    public ConsumeChannelBuilder Consumes<T>(Action<MessageConsumptionBuilder<T>> configure) where T : notnull
    {
        AddMessage<T>(configure: null);
        var messageRegistration = channel.Messages.Last();

        var consumptionBuilder = new MessageConsumptionBuilder<T>(services, messageRegistration);
        configure(consumptionBuilder);

        ValidateAndSetHandlers<T>(messageRegistration, consumptionBuilder);

        return this;
    }

    /// <summary>
    /// Registers a message type consumed from this channel, with handler registrations and message configuration.
    /// At least one handler must be registered via <c>WithHandler</c>.
    /// </summary>
    public ConsumeChannelBuilder Consumes<T>(Action<MessageConsumptionBuilder<T>> configureHandlers, Action<MessageBuilder> configureMessage) where T : notnull
    {
        AddMessage<T>(configureMessage);
        var messageRegistration = channel.Messages.Last();

        var consumptionBuilder = new MessageConsumptionBuilder<T>(services, messageRegistration);
        configureHandlers(consumptionBuilder);

        ValidateAndSetHandlers<T>(messageRegistration, consumptionBuilder);

        return this;
    }

    private static void ValidateAndSetHandlers<T>(MessageRegistration messageRegistration, MessageConsumptionBuilder<T> consumptionBuilder)
        where T : notnull
    {
        if (consumptionBuilder.HandlerRegistrations.Count == 0)
            throw new InvalidOperationException(
                $"Consumes<{typeof(T).Name}>() requires at least one handler. " +
                $"Call .WithHandler<THandler>() to register a handler.");

        messageRegistration.SetExtension(new MessageHandlerRegistrations(consumptionBuilder.HandlerRegistrations));
    }

    /// <summary>
    /// Registers a message type consumed from this channel with optional message configuration but no handlers.
    /// </summary>
    public ConsumeChannelBuilder Consumes<T>(Action<MessageBuilder>? configure = null)
    {
        AddMessage<T>(configure);
        return this;
    }
}

/// <summary>
/// Stores handler registrations on a <see cref="MessageRegistration"/> as an extension.
/// </summary>
internal class MessageHandlerRegistrations(List<ChannelHandlerRegistration> handlers)
{
    public IReadOnlyList<ChannelHandlerRegistration> Handlers => handlers;
}
