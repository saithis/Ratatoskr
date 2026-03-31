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
}
