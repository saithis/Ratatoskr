using Ratatoskr.AsyncApi.Config;

namespace Ratatoskr.AsyncApi.Attributes;

/// <summary>
/// Decorates a message class with AsyncAPI documentation metadata.
/// Takes precedence over options set via <c>MessageBuilder.WithAsyncApi()</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class AsyncApiMessageAttribute : Attribute
{
    /// <summary>
    /// EventCatalog message version (x-eventcatalog-message-version).
    /// </summary>
    public string? Version { get; set; }

    /// <summary>Human-readable message title.</summary>
    public string? Title { get; set; }

    /// <summary>Short description of the message.</summary>
    public string? Description { get; set; }
}
