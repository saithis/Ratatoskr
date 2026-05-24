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
public class AsyncApiOperationOptions
{
    /// <summary>Custom operationId. Defaults to channel name (grouped) or {action}{TypeName} (per-message).</summary>
    public string? Id { get; private set; }

    public string? Title { get; private set; }
    public string? Summary { get; private set; }
    public string? Description { get; private set; }
    public List<string>? Tags { get; private set; }

    public AsyncApiOperationOptions WithId(string id)
    {
        Id = id;
        return this;
    }

    public AsyncApiOperationOptions WithTitle(string title)
    {
        Title = title;
        return this;
    }

    public AsyncApiOperationOptions WithSummary(string summary)
    {
        Summary = summary;
        return this;
    }

    public AsyncApiOperationOptions WithDescription(string description)
    {
        Description = description;
        return this;
    }

    public AsyncApiOperationOptions WithTags(params string[] tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        Tags ??= [];
        Tags.AddRange(tags);
        return this;
    }
}
