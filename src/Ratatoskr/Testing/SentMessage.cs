using System.Text;
using System.Text.Json;
using Ratatoskr.Core;

namespace Ratatoskr.Testing;

/// <summary>
/// Represents a message that was captured by the <see cref="MessageSink"/>.
/// Contains the raw serialized content, CloudEvents properties, and timestamp.
/// </summary>
public record SentMessage(byte[] Content, MessageProperties Properties, DateTimeOffset SentAt)
{
    /// <summary>
    /// Deserializes the message content to the specified type.
    /// </summary>
    public T? Deserialize<T>(JsonSerializerOptions? options = null)
    {
        return JsonSerializer.Deserialize<T>(Content, options);
    }

    /// <summary>
    /// Gets the content as a string (assumes UTF-8 encoding).
    /// </summary>
    public string ContentAsString => Encoding.UTF8.GetString(Content);
}

/// <summary>
/// A strongly-typed wrapper around a sent message, providing direct access
/// to the deserialized message content alongside its properties.
/// </summary>
/// <typeparam name="T">The deserialized message type.</typeparam>
public record SentMessage<T>(T Message, MessageProperties Properties, DateTimeOffset SentAt);
