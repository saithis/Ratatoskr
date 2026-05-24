using System.Diagnostics.CodeAnalysis;
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

[SuppressMessage(
    "Design",
    "CA1002:Do not expose generic lists",
    Justification = "DTO for JSON serialization"
)]
[SuppressMessage(
    "Usage",
    "CA2227:CollectionPropertiesShouldBeReadOnly",
    Justification = "DTO for JSON serialization"
)]
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
    /// Like cc but consumers will not receive this information.
    /// </summary>
    [JsonPropertyName("bcc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Bcc { get; set; }

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
    public AmqpDeliveryMode? DeliveryMode { get; set; }

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

/// <summary>
/// AMQP delivery mode. Transient (1) or persistent (2).
/// </summary>
public enum AmqpDeliveryMode
{
    /// <summary>No delivery mode specified (0).</summary>
    None = 0,

    /// <summary>Transient delivery mode (1). Messages may be lost on broker restart.</summary>
    Transient = 1,

    /// <summary>Persistent delivery mode (2). Messages survive broker restarts.</summary>
    Persistent = 2,
}
