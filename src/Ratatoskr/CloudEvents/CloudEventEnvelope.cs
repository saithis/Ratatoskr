using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ratatoskr.CloudEvents;

/// <summary>
/// CloudEvents envelope in structured content mode.
/// See: <see href="https://github.com/cloudevents/spec/blob/main/cloudevents/spec.md#context-attributes" />
/// </summary>
public sealed class CloudEventEnvelope
{
    /// <summary>
    /// [REQUIRED] The version of the CloudEvents specification which the event uses.
    /// This enables the interpretation of the context. Compliant event producers MUST use a value of "1.0"
    /// when referring to this version of the specification.
    /// </summary>
    [JsonPropertyName("specversion")]
    public string SpecVersion { get; init; } = CloudEventsConstants.SpecVersion;

    /// <summary>
    /// [REQUIRED] Identifies the event. Producers MUST ensure that source + id is unique for each distinct event.
    /// If a duplicate event is re-sent (e.g. due to a network error) it MAY have the same id.
    /// Consumers MAY assume that Events with identical source and id are duplicates.
    /// </summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>
    /// [REQUIRED] Identifies the context in which an event happened. Often this will include information
    /// such as the type of the event source, the organization publishing the event or the process that produced the event.
    /// The exact syntax and semantics behind the data encoded in the URI is defined by the event producer.
    /// </summary>
    [JsonPropertyName("source")]
    public required string Source { get; init; }

    /// <summary>
    /// [REQUIRED] This attribute contains a value describing the type of event related to the originating occurrence.
    /// Often this attribute is used for routing, observability, policy enforcement, etc.
    /// SHOULD be prefixed with a reverse-DNS name.
    /// </summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>
    /// [OPTIONAL] Timestamp of when the occurrence happened. Must adhere to RFC 3339.
    /// </summary>
    [JsonPropertyName("time")]
    public DateTimeOffset? Time { get; init; }

    /// <summary>
    /// [OPTIONAL] Content type of data value. This attribute enables data to carry any type of content,
    /// whereby format and encoding might differ from that of the chosen event format.
    /// </summary>
    [JsonPropertyName("datacontenttype")]
    public string? DataContentType { get; init; }

    /// <summary>
    /// [OPTIONAL] Identifies the schema that data adheres to. Incompatible changes to the schema SHOULD be reflected by a different URI.
    /// </summary>
    [JsonPropertyName("dataschema")]
    public string? DataSchema { get; init; }

    /// <summary>
    /// [OPTIONAL] This identifies the subject of the event in the context of the event producer (identified by source).
    /// Identifying the subject of the event in context metadata is particularly helpful in generic subscription filtering scenarios.
    /// </summary>
    [JsonPropertyName("subject")]
    public string? Subject { get; init; }

    /// <summary>
    /// The event data (payload).
    /// </summary>
    [JsonPropertyName("data")]
    public object? Data { get; init; }

    /// <summary>
    /// Extension attributes (custom fields).
    /// Extension attributes MUST follow the same naming convention and use the same type system as standard attributes.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, object>? Extensions { get; init; }

    public bool TryGetExtension<T>(string keyName, out T? value)
    {
        value = default;
        if (Extensions == null || !Extensions.TryGetValue(keyName, out var obj))
        {
            return false;
        }

        if (obj is T t)
        {
            value = t;
            return true;
        }

        if (obj is JsonElement element)
        {
            try
            {
                value = element.Deserialize<T>();
                return true;
            }
            catch (JsonException)
            {
                // Ignore deserialization errors and return false
            }
        }

        return false;
    }
}
