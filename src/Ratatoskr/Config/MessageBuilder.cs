using Ratatoskr.Core;

namespace Ratatoskr.Config;

public class MessageBuilder(MessageRegistration message)
{
    internal MessageRegistration MessageRegistration => message;

    public MessageBuilder WithType(string typeName)
    {
        message.MessageTypeName = typeName;
        return this;
    }

    public MessageBuilder WithDataSchema(string dataSchema)
    {
        message.DataSchema = dataSchema;
        return this;
    }

    public MessageBuilder WithSerializer<TSerializer>() where TSerializer : class, IMessageSerializer
    {
        message.SerializerType = typeof(TSerializer);
        return this;
    }
}
