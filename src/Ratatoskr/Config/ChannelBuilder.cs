using System.Reflection;
using Ratatoskr.Core;

namespace Ratatoskr.Config;

/// <summary>
/// Base class for fluent channel configuration builders.
/// </summary>
public abstract class ChannelBuilder(ChannelRegistration channel)
{
    /// <summary>The underlying channel registration.</summary>
    internal ChannelRegistration Channel { get; } = channel;

    /// <summary>
    /// Sets a typed extension on the channel registration.
    /// Used by transport providers to attach transport-specific configuration.
    /// </summary>
    protected internal ChannelBuilder WithExtension<T>(T value)
        where T : class
    {
        Channel.SetExtension(value);
        return this;
    }

    /// <summary>
    /// Registers a transport on this channel.
    /// Used by transport providers to declare which transports handle this channel.
    /// </summary>
    protected internal ChannelBuilder AddTransport(string transportName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transportName);
        _ = Channel.Transports.Add(transportName);
        return this;
    }

    internal void AddMessage<T>(Action<MessageBuilder>? configure, string? typeName = null)
    {
        var type = typeof(T);
        typeName ??= GetMessageTypeName(type);

        if (Channel.Messages.Any(r => r.MessageType == type))
        {
            throw new InvalidOperationException(
                $"Message type '{type.FullName}' is already registered on this channel."
            );
        }

        var registration = new MessageRegistration(type, typeName);
        registration.DataSchema = GetDataSchema(type);

        if (configure != null)
        {
            var builder = new MessageBuilder(registration);
            configure(builder);
        }

        Channel.Messages.Add(registration);
    }

    private static string GetMessageTypeName(Type type)
    {
        var attr = type.GetCustomAttribute<RatatoskrMessageAttribute>();
        return attr?.Type ?? type.Name;
    }

    private static string? GetDataSchema(Type type)
    {
        var attr = type.GetCustomAttribute<RatatoskrMessageAttribute>();
        return attr?.DataSchema;
    }
}
