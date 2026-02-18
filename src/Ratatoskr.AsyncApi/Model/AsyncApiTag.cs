using System.Text.Json.Serialization;

namespace Ratatoskr.AsyncApi.Model;

public class AsyncApiTag
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }
}
