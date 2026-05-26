namespace Ratatoskr.EfCore;

public class MessagePropertiesDeserializationException : OutboxException
{
    /// <summary>
    /// The message properties, that could not be deserialized.
    /// </summary>
    public string SerializedProperties { get; } = string.Empty;

    public MessagePropertiesDeserializationException() { }

    public MessagePropertiesDeserializationException(string message)
        : base(message) { }

    public MessagePropertiesDeserializationException(string message, Exception innerException)
        : base(message, innerException) { }

    public MessagePropertiesDeserializationException(string message, string serializedProperties)
        : base(message)
    {
        SerializedProperties = serializedProperties;
    }

    public MessagePropertiesDeserializationException(
        string message,
        string serializedProperties,
        Exception innerException
    )
        : base(message, innerException)
    {
        SerializedProperties = serializedProperties;
    }
}
