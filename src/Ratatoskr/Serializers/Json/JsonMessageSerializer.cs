using System.Text.Json;
using Ratatoskr.Core;

namespace Ratatoskr.Serializers.Json;

public class JsonMessageSerializer : IMessageSerializer
{
    private readonly JsonSerializerOptions? _options;

    public JsonMessageSerializer() { }

    public JsonMessageSerializer(JsonSerializerOptions options)
    {
        _options = options;
    }

    public string ContentType => "application/json";

    public byte[] Serialize(object message)
    {
        return JsonSerializer.SerializeToUtf8Bytes(message, _options);
    }

    public object? Deserialize(byte[] body, Type targetType)
    {
        return JsonSerializer.Deserialize(body, targetType, _options);
    }

    public TMessage? Deserialize<TMessage>(byte[] body)
    {
        return (TMessage?)Deserialize(body, typeof(TMessage));
    }
}