using System.Text.Json.Serialization;

namespace Ratatoskr.AsyncApi.Model.Bindings;

public class OperationBindings
{
    /// <summary>
    /// This object contains information about the operation representation in AMQP.
    /// </summary>
    [JsonPropertyName("amqp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AmqpOperationBinding? Amqp { get; set; }
}

public class AmqpOperationBinding
{
    /// <summary>
    /// TTL (Time-To-Live) for the message. It MUST be greater than or equal to zero.
    /// </summary>
    [JsonPropertyName("expiration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Expiration { get; set; }

    /// <summary>
    /// Identifies the user who has sent the message.
    /// </summary>
    [JsonPropertyName("userId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UserId { get; set; }

    /// <summary>
    /// The routing keys the message should be routed to at the time of publishing.
    /// </summary>
    [JsonPropertyName("cc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Cc { get; set; }

    /// <summary>
    /// A priority for the message.
    /// </summary>
    [JsonPropertyName("priority")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Priority { get; set; }

    /// <summary>
    /// Delivery mode of the message. Its value MUST be either 1 (transient) or 2 (persistent).
    /// </summary>
    [JsonPropertyName("deliveryMode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DeliveryMode { get; set; }

    /// <summary>
    /// Whether the message is mandatory or not.
    /// </summary>
    [JsonPropertyName("mandatory")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Mandatory { get; set; }

    /// <summary>
    /// Whether the message should include a timestamp or not.
    /// </summary>
    [JsonPropertyName("timestamp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Timestamp { get; set; }

    /// <summary>
    /// Whether the consumer should ack the message or not.
    /// </summary>
    [JsonPropertyName("ack")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Ack { get; set; }

    /// <summary>
    /// The version of this binding. If omitted, "latest" MUST be assumed.
    /// </summary>
    [JsonPropertyName("bindingVersion")]
    public string BindingVersion { get; set; } = "0.3.0";
}
