using System.Text.Json.Serialization;

namespace Ratatoskr.AsyncApi.Model.Bindings;

public class OperationBindings
{
    [JsonPropertyName("amqp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AmqpOperationBinding? Amqp { get; set; }
}

public class AmqpOperationBinding
{
    [JsonPropertyName("expiration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Expiration { get; set; }

    [JsonPropertyName("userId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UserId { get; set; }

    /// <summary>Routing keys the message is sent to in addition to the channel.</summary>
    [JsonPropertyName("cc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Cc { get; set; }

    [JsonPropertyName("priority")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Priority { get; set; }

    /// <summary>1 = transient, 2 = persistent.</summary>
    [JsonPropertyName("deliveryMode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DeliveryMode { get; set; }

    [JsonPropertyName("mandatory")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Mandatory { get; set; }

    [JsonPropertyName("timestamp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Timestamp { get; set; }

    [JsonPropertyName("ack")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Ack { get; set; }

    [JsonPropertyName("bindingVersion")]
    public string BindingVersion { get; set; } = "0.3.0";
}
