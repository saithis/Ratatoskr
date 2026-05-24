namespace Ratatoskr.AsyncApi.Config;

/// <summary>
/// AsyncAPI documentation options for a channel.
/// Configure via <c>.WithAsyncApi(x => x.WithDescription("..."))</c> on a ChannelBuilder.
/// </summary>
public sealed class AsyncApiChannelOptions
{
    // --- Channel metadata ---

    /// <summary>Optional title for the channel in the AsyncAPI document.</summary>
    public string? Title { get; private set; }

    /// <summary>Optional short summary of the channel.</summary>
    public string? Summary { get; private set; }

    /// <summary>Optional detailed description of the channel.</summary>
    public string? Description { get; private set; }

    // --- Channel-level operation (opt-in grouping) ---

    /// <summary>
    /// When set, all messages on this channel are grouped into a single operation.
    /// When not set, each message gets its own operation (default).
    /// </summary>
    public AsyncApiOperationOptions? Operation { get; private set; }

    /// <summary>Sets the channel title and returns this instance for chaining.</summary>
    public AsyncApiChannelOptions WithTitle(string title)
    {
        Title = title;
        return this;
    }

    /// <summary>Sets the channel summary and returns this instance for chaining.</summary>
    public AsyncApiChannelOptions WithSummary(string summary)
    {
        Summary = summary;
        return this;
    }

    /// <summary>Sets the channel description and returns this instance for chaining.</summary>
    public AsyncApiChannelOptions WithDescription(string description)
    {
        Description = description;
        return this;
    }

    /// <summary>
    /// Configures a channel-level operation, grouping all messages into one operation.
    /// The operationId defaults to the channel name unless overridden with <c>WithId()</c>.
    /// </summary>
    public AsyncApiChannelOptions WithOperation(Action<AsyncApiOperationOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        Operation = new AsyncApiOperationOptions();
        configure(Operation);
        return this;
    }
}
