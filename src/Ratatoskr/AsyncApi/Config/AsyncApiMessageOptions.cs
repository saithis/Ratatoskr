namespace Ratatoskr.AsyncApi.Config;

/// <summary>
/// AsyncAPI documentation options for a message registration.
/// Configure via <c>.WithAsyncApi(x => x.WithVersion("1.0.0"))</c> on a MessageBuilder.
/// </summary>
public class AsyncApiMessageOptions
{
    /// <summary>Optional title for the message in the AsyncAPI document.</summary>
    public string? Title { get; private set; }

    /// <summary>Optional short summary of the message.</summary>
    public string? Summary { get; private set; }

    /// <summary>Optional detailed description of the message.</summary>
    public string? Description { get; private set; }

    /// <summary>
    /// EventCatalog message version (x-eventcatalog-message-version).
    /// Defaults to "1.0.0" if not specified.
    /// </summary>
    public string? Version { get; private set; }

    /// <summary>Sets the message title and returns this instance for chaining.</summary>
    public AsyncApiMessageOptions WithTitle(string title)
    {
        Title = title;
        return this;
    }

    /// <summary>Sets the message summary and returns this instance for chaining.</summary>
    public AsyncApiMessageOptions WithSummary(string summary)
    {
        Summary = summary;
        return this;
    }

    /// <summary>Sets the message description and returns this instance for chaining.</summary>
    public AsyncApiMessageOptions WithDescription(string description)
    {
        Description = description;
        return this;
    }

    /// <summary>Sets the EventCatalog message version and returns this instance for chaining.</summary>
    public AsyncApiMessageOptions WithVersion(string version)
    {
        Version = version;
        return this;
    }

    // --- Operation metadata ---

    /// <summary>
    /// Operation options for this specific message. Only applies when the channel
    /// does not have a channel-level operation configured (per-message mode).
    /// Messages sharing the same operationId are merged into a single operation.
    /// </summary>
    public AsyncApiOperationOptions? Operation { get; private set; }

    /// <summary>
    /// Customizes the operation generated for this message.
    /// Ignored if the channel has a channel-level <c>WithOperation()</c> configured.
    /// </summary>
    public AsyncApiMessageOptions WithOperation(Action<AsyncApiOperationOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        Operation = new AsyncApiOperationOptions();
        configure(Operation);
        return this;
    }
}
