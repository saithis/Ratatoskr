using System.Text.Json;
using Ratatoskr.Core;

namespace Ratatoskr.Serializers.Json;

/// <summary>
/// Serializes and deserializes messages using System.Text.Json.
/// </summary>
public sealed class JsonMessageSerializer : IMessageSerializer
{
    private readonly JsonSerializerOptions? _options;

    /// <summary>Initializes the serializer with default <see cref="JsonSerializerOptions"/>.</summary>
    public JsonMessageSerializer()
        : this(options: null) { }

    /// <summary>Initializes the serializer with the specified <see cref="JsonSerializerOptions"/>.</summary>
    public JsonMessageSerializer(JsonSerializerOptions? options) => _options = options;

    /// <inheritdoc/>
    public string ContentType => "application/json";

    /// <inheritdoc/>
    public byte[] Serialize(object message)
    {
        return JsonSerializer.SerializeToUtf8Bytes(message, _options);
    }

    /// <inheritdoc/>
    public object? Deserialize(byte[] body, Type targetType)
    {
        return JsonSerializer.Deserialize(body, targetType, _options);
    }

    /// <inheritdoc/>
    public TMessage? Deserialize<TMessage>(byte[] body)
    {
        return (TMessage?)Deserialize(body, typeof(TMessage));
    }
}
