using System.Text.Json;
using Ratatoskr.Core;

namespace Ratatoskr.EfCore.Internal;

/// <summary>
/// Persisted record of a message received from any transport.
/// One row per unique CloudEvents <c>id</c> — acts as the deduplication anchor.
/// </summary>
internal class InboxMessageEntity
{
    /// <summary>CloudEvents "id" of the received message. Primary key and deduplication key.</summary>
    public string Id { get; private set; } = string.Empty;

    /// <summary>Name of the transport that delivered the message (e.g. "local", "rabbitmq").</summary>
    public string TransportName { get; private set; } = string.Empty;

    public required byte[] Content { get; init; }

    /// <summary>JSON-serialized <see cref="MessageProperties"/>.</summary>
    public required string SerializedProperties { get; init; }

    public required DateTimeOffset ReceivedAt { get; init; }

    public MessageProperties GetProperties() =>
        JsonSerializer.Deserialize<MessageProperties>(SerializedProperties)
        ?? throw new InvalidOperationException($"Could not deserialize MessageProperties for inbox message '{Id}'.");

    internal const int MaxIdLength = 200;

    /// <summary>
    /// Validates that the message ID does not exceed the database column length.
    /// Call before attempting to insert to get a clear error instead of a DB truncation/failure.
    /// </summary>
    public static void ValidateIdLength(string messageId)
    {
        if (messageId.Length > MaxIdLength)
            throw new InvalidOperationException(
                $"Message ID exceeds the maximum length of {MaxIdLength} characters (actual: {messageId.Length}). " +
                $"ID: '{messageId[..50]}...'");
    }

    private InboxMessageEntity() { }

    public static InboxMessageEntity Create(
        string messageId,
        string transportName,
        byte[] content,
        MessageProperties props,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(props);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(transportName);
        ValidateIdLength(messageId);
        return new InboxMessageEntity
        {
            Id = messageId,
            TransportName = transportName,
            Content = content,
            SerializedProperties = JsonSerializer.Serialize(props),
            ReceivedAt = timeProvider.GetUtcNow(),
        };
    }
}
