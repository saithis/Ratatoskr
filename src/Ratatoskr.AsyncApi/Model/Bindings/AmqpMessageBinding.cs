using System.Text.Json.Serialization;

namespace Ratatoskr.AsyncApi.Model.Bindings;

public class MessageBindings
{
    [JsonPropertyName("amqp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AmqpMessageBinding? Amqp { get; set; }
}

public class AmqpMessageBinding
{
    [JsonPropertyName("contentEncoding")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContentEncoding { get; set; }

    [JsonPropertyName("messageType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MessageType { get; set; }

    [JsonPropertyName("bindingVersion")]
    public string BindingVersion { get; set; } = "0.3.0";
}
