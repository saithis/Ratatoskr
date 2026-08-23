using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Ratatoskr.Core;

namespace Ratatoskr.EfCore.Internal;

internal class BaseMessageEntity
{
    /// <summary>JSON-serialized <see cref="MessageProperties"/>.</summary>
    public required string SerializedProperties { get; init; }

    public required byte[] Content { get; set; }

    /// <summary>
    /// outbox: The transport this outbox entry targets.<br />
    /// inbox: The transport that delivered the message.<br />
    /// e.g. "rabbitmq", "efcore".
    /// </summary>
    [MaxLength(50)]
    public required string TransportName { get; init; } = string.Empty;

    private MessageProperties? _cachedProperties;

    public MessageProperties GetProperties() =>
        _cachedProperties ??= DeserializeMessageProperties(SerializedProperties);

    internal static MessageProperties DeserializeMessageProperties(string serializedProperties) =>
        JsonSerializer.Deserialize<MessageProperties>(serializedProperties)
        ?? throw new MessagePropertiesDeserializationException(
            "Could not deserialize MessageProperties",
            serializedProperties
        );
}
