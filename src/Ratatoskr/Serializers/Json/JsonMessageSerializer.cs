using System.Text.Json;
using Ratatoskr.Core;

namespace Ratatoskr.Serializers.Json;

public sealed class JsonMessageSerializer : IMessageSerializer
{
    private readonly JsonSerializerOptions? _options;

    public JsonMessageSerializer()
        : this(options: null) { }

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
