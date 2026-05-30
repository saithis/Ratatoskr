using Ratatoskr.Core;

namespace Ratatoskr.Config;

/// <summary>
/// Fluent builder for configuring a message registration.
/// </summary>
public sealed class MessageBuilder(MessageRegistration message)
{
    internal MessageRegistration MessageRegistration => message;

    /// <summary>Overrides the message type name used as the CloudEvents type identifier.</summary>
    public MessageBuilder WithType(string typeName)
    {
        message.MessageTypeName = typeName;
        return this;
    }

    /// <summary>Sets the URI identifying the schema the message data adheres to.</summary>
    public MessageBuilder WithDataSchema(string dataSchema)
    {
        message.DataSchema = dataSchema;
        return this;
    }

    /// <summary>Registers a specific serializer type to use for this message.</summary>
    public MessageBuilder WithSerializer<TSerializer>()
        where TSerializer : class, IMessageSerializer
    {
        message.SerializerType = typeof(TSerializer);
        return this;
    }
}
