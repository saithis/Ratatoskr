using System.Text.Json.Serialization;

namespace Ratatoskr.AsyncApi.Model.Bindings;

/// <summary>
/// Container for transport-specific message binding definitions.
/// </summary>
public sealed class MessageBindings
{
    /// <summary>
    /// This object contains information about the message representation in AMQP.
    /// </summary>
    [JsonPropertyName("amqp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AmqpMessageBinding? Amqp { get; set; }
}

/// <summary>
/// AMQP-specific message binding properties.
/// </summary>
public sealed class AmqpMessageBinding
{
    /// <summary>
    /// A MIME encoding for the message content. (example: gzip)
    /// </summary>
    [JsonPropertyName("contentEncoding")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContentEncoding { get; set; }

    /// <summary>
    /// Application-specific message type.
    /// </summary>
    [JsonPropertyName("messageType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MessageType { get; set; }

    /// <summary>
    /// The version of this binding. If omitted, "latest" MUST be assumed.
    /// </summary>
    [JsonPropertyName("bindingVersion")]
    public string BindingVersion { get; set; } = "0.3.0";
}
