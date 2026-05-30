using System.Text.Json.Serialization;

namespace Ratatoskr.AsyncApi.Model;

/// <summary>
/// Metadata about the API contained in the AsyncAPI document's info section.
/// </summary>
public sealed class AsyncApiInfo
{
    /// <summary>The title of the API.</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    /// <summary>The version of the API.</summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = "0.0.0";

    /// <summary>A short description of the API.</summary>
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    /// <summary>Contact information for the API maintainers.</summary>
    [JsonPropertyName("contact")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AsyncApiContact? Contact { get; set; }
}

/// <summary>
/// Contact information for the owners or maintainers of the API.
/// </summary>
public sealed class AsyncApiContact
{
    /// <summary>The name of the contact person or organization.</summary>
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    /// <summary>URL pointing to the contact information.</summary>
    [JsonPropertyName("url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public System.Uri? Url { get; set; }

    /// <summary>Email address of the contact person or organization.</summary>
    [JsonPropertyName("email")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Email { get; set; }
}
