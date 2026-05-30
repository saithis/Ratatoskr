using System.Text.Json.Serialization;

namespace Ratatoskr.AsyncApi.Model;

/// <summary>
/// Represents an AsyncAPI v3 server (broker) definition.
/// </summary>
public sealed class AsyncApiServer
{
    /// <summary>The host name (and optional port) of the broker.</summary>
    [JsonPropertyName("host")]
    public string Host { get; set; } = "";

    /// <summary>The messaging protocol used by the broker (e.g. "amqp", "kafka").</summary>
    [JsonPropertyName("protocol")]
    public string Protocol { get; set; } = "";

    /// <summary>The version of the protocol used (e.g. "0-9-1" for AMQP).</summary>
    [JsonPropertyName("protocolVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProtocolVersion { get; set; }

    /// <summary>A description of the server.</summary>
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }
}
