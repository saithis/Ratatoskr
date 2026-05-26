namespace Ratatoskr.EfCore;

public class MessagePropertiesDeserializationException(string message, string serializedProperties)
    : OutboxException(message)
{
    /// <summary>
    /// The message properties, that could not be deserialized.
    /// </summary>
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    public string SerializedProperties { get; } = serializedProperties;
}
