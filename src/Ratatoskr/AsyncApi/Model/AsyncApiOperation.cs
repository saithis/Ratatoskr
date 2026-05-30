using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Ratatoskr.AsyncApi.Model.Bindings;

namespace Ratatoskr.AsyncApi.Model;

/// <summary>
/// Represents an AsyncAPI v3 operation describing how a message is sent or received on a channel.
/// </summary>
[SuppressMessage(
    "Design",
    "CA1002:Do not expose generic lists",
    Justification = "DTO for JSON serialization"
)]
[SuppressMessage(
    "Usage",
    "CA2227:CollectionPropertiesShouldBeReadOnly",
    Justification = "DTO for JSON serialization"
)]
public sealed class AsyncApiOperation
{
    /// <summary>"send" for publish, "receive" for consume.</summary>
    [JsonPropertyName("action")]
    public string Action { get; set; } = "send";

    /// <summary>Reference to the channel this operation acts on.</summary>
    [JsonPropertyName("channel")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AsyncApiReference? Channel { get; set; }

    /// <summary>Human-readable title of the operation.</summary>
    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; set; }

    /// <summary>Short summary of what the operation does.</summary>
    [JsonPropertyName("summary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Summary { get; set; }

    /// <summary>Detailed description of the operation.</summary>
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    /// <summary>
    /// Subset of channel messages this operation handles.
    /// </summary>
    [JsonPropertyName("messages")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AsyncApiReference>? Messages { get; set; }

    /// <summary>Transport-specific binding information for the operation.</summary>
    [JsonPropertyName("bindings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OperationBindings? Bindings { get; set; }

    /// <summary>Tags that categorize the operation.</summary>
    [JsonPropertyName("tags")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AsyncApiTag>? Tags { get; set; }
}
