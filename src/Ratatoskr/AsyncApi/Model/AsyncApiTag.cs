using System.Text.Json.Serialization;

namespace Ratatoskr.AsyncApi.Model;

/// <summary>
/// Represents an AsyncAPI v3 tag used to categorize operations and channels.
/// </summary>
public sealed class AsyncApiTag
{
    /// <summary>The name of the tag.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>An optional description of the tag.</summary>
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }
}
