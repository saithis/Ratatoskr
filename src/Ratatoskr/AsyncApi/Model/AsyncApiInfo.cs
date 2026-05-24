using System.Text.Json.Serialization;

namespace Ratatoskr.AsyncApi.Model;

public sealed class AsyncApiInfo
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "0.0.0";

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("contact")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AsyncApiContact? Contact { get; set; }
}

public sealed class AsyncApiContact
{
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    [JsonPropertyName("url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Url { get; set; }

    [JsonPropertyName("email")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Email { get; set; }
}
