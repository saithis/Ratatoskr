using System.Text.Json;
using System.Text.Json.Serialization;
using Ratatoskr.AsyncApi.Model.Bindings;

namespace Ratatoskr.AsyncApi.Model;

public class AsyncApiChannel
{
    [JsonPropertyName("address")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Address { get; set; }

    /// <summary>
    /// Channel messages map (key = message name, value = $ref to components/messages).
    /// </summary>
    [JsonPropertyName("messages")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, AsyncApiReference>? Messages { get; set; }

    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; set; }

    [JsonPropertyName("summary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Summary { get; set; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    /// <summary>
    /// Server references for this channel.
    /// </summary>
    [JsonPropertyName("servers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AsyncApiReference>? Servers { get; set; }

    [JsonPropertyName("bindings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ChannelBindings? Bindings { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}
