namespace Ratatoskr.EfCore;

/// <summary>
/// Exception thrown when message properties cannot be deserialized from their stored representation.
/// </summary>
/// <param name="message">The error message describing the deserialization failure.</param>
/// <param name="serializedProperties">The raw serialized properties that could not be deserialized.</param>
public class MessagePropertiesDeserializationException(string message, string serializedProperties)
    : OutboxException(message)
{
    /// <summary>
    /// The message properties, that could not be deserialized.
    /// </summary>
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    public string SerializedProperties { get; } = serializedProperties;
}
