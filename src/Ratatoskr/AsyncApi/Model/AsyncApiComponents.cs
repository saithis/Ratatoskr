using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Ratatoskr.AsyncApi.Model;

/// <summary>
/// Holds reusable AsyncAPI v3 component definitions (schemas, messages, servers).
/// </summary>
[SuppressMessage(
    "Usage",
    "CA2227:CollectionPropertiesShouldBeReadOnly",
    Justification = "DTO for JSON serialization"
)]
public sealed class AsyncApiComponents
{
    /// <summary>Reusable JSON Schema definitions referenced by message payloads.</summary>
    [JsonPropertyName("schemas")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, JsonSchema>? Schemas { get; set; }

    /// <summary>Reusable message definitions referenced by channel message maps.</summary>
    [JsonPropertyName("messages")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, AsyncApiMessage>? Messages { get; set; }

    /// <summary>Reusable server definitions referenced by channels.</summary>
    [JsonPropertyName("servers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, AsyncApiServer>? Servers { get; set; }
}
