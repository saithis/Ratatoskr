using System.Diagnostics.CodeAnalysis;

namespace Ratatoskr.AsyncApi.Config;

/// <summary>
/// AsyncAPI documentation options for an operation.
/// Can be set at channel level (groups all messages into one operation)
/// or at message level (customizes the per-message operation).
/// Messages sharing the same <see cref="Id"/> are merged into a single operation.
/// </summary>
[SuppressMessage(
    "Design",
    "CA1002:Do not expose generic lists",
    Justification = "DTO for API configuration"
)]
public sealed class AsyncApiOperationOptions
{
    /// <summary>Custom operationId. Defaults to channel name (grouped) or {action}{TypeName} (per-message).</summary>
    public string? Id { get; private set; }

    /// <summary>Human-readable title for the operation.</summary>
    public string? Title { get; private set; }

    /// <summary>Short summary of what the operation does.</summary>
    public string? Summary { get; private set; }

    /// <summary>Detailed description of the operation.</summary>
    public string? Description { get; private set; }

    /// <summary>Tags that categorize the operation.</summary>
    public List<string>? Tags { get; private set; }

    /// <summary>Sets a custom operationId for the operation.</summary>
    public AsyncApiOperationOptions WithId(string id)
    {
        Id = id;
        return this;
    }

    /// <summary>Sets the human-readable title for the operation.</summary>
    public AsyncApiOperationOptions WithTitle(string title)
    {
        Title = title;
        return this;
    }

    /// <summary>Sets a short summary of what the operation does.</summary>
    public AsyncApiOperationOptions WithSummary(string summary)
    {
        Summary = summary;
        return this;
    }

    /// <summary>Sets a detailed description of the operation.</summary>
    public AsyncApiOperationOptions WithDescription(string description)
    {
        Description = description;
        return this;
    }

    /// <summary>Adds one or more tags to categorize the operation.</summary>
    public AsyncApiOperationOptions WithTags(params string[] tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        Tags ??= [];
        Tags.AddRange(tags);
        return this;
    }
}
