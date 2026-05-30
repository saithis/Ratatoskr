using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Ratatoskr.AsyncApi.Model;

/// <summary>
/// Root object of an AsyncAPI v3 document.
/// </summary>
[SuppressMessage(
    "Usage",
    "CA2227:CollectionPropertiesShouldBeReadOnly",
    Justification = "DTO for JSON serialization"
)]
public sealed class AsyncApiDocument
{
    /// <summary>The AsyncAPI specification version used by this document (e.g. "3.0.0").</summary>
    [JsonPropertyName("asyncapi")]
    public string AsyncApi { get; set; } = "3.0.0";

    /// <summary>Metadata about the API (title, version, description, contact).</summary>
    [JsonPropertyName("info")]
    public AsyncApiInfo Info { get; set; } = new();

    /// <summary>Server definitions (brokers) referenced by channels.</summary>
    [JsonPropertyName("servers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, AsyncApiServer>? Servers { get; set; }

    /// <summary>All channel definitions keyed by channel name.</summary>
    [JsonPropertyName("channels")]
    public Dictionary<string, AsyncApiChannel> Channels { get; set; } = new(StringComparer.Ordinal);

    /// <summary>All operation definitions keyed by operationId.</summary>
    [JsonPropertyName("operations")]
    public Dictionary<string, AsyncApiOperation> Operations { get; set; } =
        new(StringComparer.Ordinal);

    /// <summary>Reusable component definitions (schemas, messages, servers).</summary>
    [JsonPropertyName("components")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AsyncApiComponents? Components { get; set; }
}
