namespace Ratatoskr.AsyncApi.Config;

/// <summary>
/// AsyncAPI documentation options for a message registration.
/// Configure via <c>.WithAsyncApi(x => x.WithVersion("1.0.0"))</c> on a MessageBuilder.
/// </summary>
public class AsyncApiMessageOptions
{
    public string? Title { get; private set; }
    public string? Summary { get; private set; }
    public string? Description { get; private set; }

    /// <summary>
    /// EventCatalog message version (x-eventcatalog-message-version).
    /// Defaults to "1.0.0" if not specified.
    /// </summary>
    public string? Version { get; private set; }

    public AsyncApiMessageOptions WithTitle(string title)
    {
        Title = title;
        return this;
    }

    public AsyncApiMessageOptions WithSummary(string summary)
    {
        Summary = summary;
        return this;
    }

    public AsyncApiMessageOptions WithDescription(string description)
    {
        Description = description;
        return this;
    }

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
        Operation = new AsyncApiOperationOptions();
        configure(Operation);
        return this;
    }
}
