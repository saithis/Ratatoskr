namespace Ratatoskr.Core;

public interface IMessageSerializer
{
    /// <summary>
    /// Gets the content type produced by this serializer.
    /// </summary>
    string ContentType { get; }

    /// <summary>
    /// Serializes a message.
    /// </summary>
    byte[] Serialize(object message);

    /// <summary>
    /// Deserializes a message body to the specified type.
    /// </summary>
    object? Deserialize(byte[] body, Type targetType);

    /// <summary>
    /// Deserializes a message body to the specified type.
    /// </summary>
    TMessage? Deserialize<TMessage>(byte[] body);
}
