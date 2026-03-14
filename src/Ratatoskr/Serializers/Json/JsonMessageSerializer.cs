using System.Text.Json;
using Ratatoskr.Core;

namespace Ratatoskr.Serializers.Json;

public class JsonMessageSerializer : IMessageSerializer
{
    public string ContentType => "application/json";

    public byte[] Serialize(object message)
    {
        return JsonSerializer.SerializeToUtf8Bytes(message);
    }
    
    public object? Deserialize(byte[] body, Type targetType)
    {
        return JsonSerializer.Deserialize(body, targetType);
    }
    
    public TMessage? Deserialize<TMessage>(byte[] body)
    {
        return (TMessage?)Deserialize(body, typeof(TMessage));
    }
}