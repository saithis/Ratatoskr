using System.Text.Json.Serialization;

namespace Ratatoskr.AsyncApi.Model;

public sealed class AsyncApiServer
{
    [JsonPropertyName("host")]
    public string Host { get; set; } = "";

    [JsonPropertyName("protocol")]
    public string Protocol { get; set; } = "";

    [JsonPropertyName("protocolVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProtocolVersion { get; set; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }
}
