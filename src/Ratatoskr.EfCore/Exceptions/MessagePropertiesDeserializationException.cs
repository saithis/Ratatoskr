namespace Ratatoskr.EfCore;

public class MessagePropertiesDeserializationException(string message, string serializedProperties)
    : OutboxException(message)
{
    /// <summary>
    /// The message properties, that could not be deserialized.
    /// </summary>
    public string SerializedProperties { get; } = serializedProperties;
}
