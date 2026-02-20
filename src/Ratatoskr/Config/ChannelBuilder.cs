using System.Reflection;
using Ratatoskr.Core;

namespace Ratatoskr.Config;

public class ChannelBuilder(ChannelRegistration channel)
{
    internal ChannelRegistration Channel => channel;

    /// <summary>
    /// Sets a typed extension on the channel registration.
    /// Used by transport providers to attach transport-specific configuration.
    /// </summary>
    public ChannelBuilder WithExtension<T>(T value) where T : class
    {
        channel.SetExtension(value);
        return this;
    }

    public ChannelRegistration Build() => channel;

    internal void AddMessage<T>(Action<MessageBuilder>? configure, string? typeName = null)
    {
        var type = typeof(T);
        typeName ??= GetMessageTypeName(type);

        var registration = new MessageRegistration(type, typeName);

        if (configure != null)
        {
            var builder = new MessageBuilder(registration);
            configure(builder);
        }

        channel.Messages.Add(registration);
    }

    private static string GetMessageTypeName(Type type)
    {
        var attr = type.GetCustomAttribute<RatatoskrMessageAttribute>();
        return attr?.Type ?? type.Name;
    }
}
