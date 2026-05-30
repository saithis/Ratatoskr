namespace Ratatoskr.Core;

/// <summary>
/// Serializes and deserializes message payloads for a specific content type.
/// </summary>
public interface IMessageSerializer
{
    /// <summary>
    /// Gets the content type produced by this serializer.
    /// </summary>
    public string ContentType { get; }

    /// <summary>
    /// Serializes a message.
    /// </summary>
    public byte[] Serialize(object message);

    /// <summary>
    /// Deserializes a message body to the specified type.
    /// </summary>
    public object? Deserialize(byte[] body, Type targetType);

    /// <summary>
    /// Deserializes a message body to the specified type.
    /// </summary>
    public TMessage? Deserialize<TMessage>(byte[] body);
}
