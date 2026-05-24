using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Ratatoskr.AsyncApi.Model;

[SuppressMessage(
    "Usage",
    "CA2227:CollectionPropertiesShouldBeReadOnly",
    Justification = "DTO for JSON serialization"
)]
public sealed class AsyncApiDocument
{
    [JsonPropertyName("asyncapi")]
    public string AsyncApi { get; set; } = "3.0.0";

    [JsonPropertyName("info")]
    public AsyncApiInfo Info { get; set; } = new();

    [JsonPropertyName("servers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, AsyncApiServer>? Servers { get; set; }

    [JsonPropertyName("channels")]
    public Dictionary<string, AsyncApiChannel> Channels { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("operations")]
    public Dictionary<string, AsyncApiOperation> Operations { get; set; } =
        new(StringComparer.Ordinal);

    [JsonPropertyName("components")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AsyncApiComponents? Components { get; set; }
}
