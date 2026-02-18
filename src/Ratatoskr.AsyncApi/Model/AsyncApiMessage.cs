using System.Text.Json;
using System.Text.Json.Serialization;
using Ratatoskr.AsyncApi.Model.Bindings;

namespace Ratatoskr.AsyncApi.Model;

public class AsyncApiMessage
{
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; set; }

    [JsonPropertyName("summary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Summary { get; set; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

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

    [JsonPropertyName("bindings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MessageBindings? Bindings { get; set; }

    /// <summary>
    /// Extension data for x-eventcatalog-* and other vendor extensions.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}
