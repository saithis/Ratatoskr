using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ratatoskr.AsyncApi.Model.Bindings;

namespace Ratatoskr.AsyncApi.Model;

/// <summary>
/// Represents an AsyncAPI v3 message definition describing the shape of a message on a channel.
/// </summary>
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
public sealed class AsyncApiMessage
{
    /// <summary>Machine-readable name of the message (typically the CLR type name).</summary>
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    /// <summary>Human-readable title of the message.</summary>
    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; set; }

    /// <summary>Short summary of the message's purpose.</summary>
    [JsonPropertyName("summary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Summary { get; set; }

    /// <summary>Detailed description of the message.</summary>
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    /// <summary>The content type of the message payload (e.g. "application/json").</summary>
    [JsonPropertyName("contentType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContentType { get; set; }

    /// <summary>
    /// The schema for the message payload (event data in binary mode, CloudEvent envelope in structured mode).
    /// </summary>
    [JsonPropertyName("payload")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonSchema? Payload { get; set; }

    /// <summary>
    /// Schema for AMQP application-properties (CloudEvents attributes in binary mode).
    /// </summary>
    [JsonPropertyName("headers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonSchema? Headers { get; set; }

    /// <summary>Transport-specific binding information for the message.</summary>
    [JsonPropertyName("bindings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MessageBindings? Bindings { get; set; }

    /// <summary>
    /// Extension data for x-eventcatalog-* and other vendor extensions.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}
