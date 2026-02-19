namespace Ratatoskr.AsyncApi.Config;

/// <summary>
/// AsyncAPI documentation options for a channel.
/// Configure via <c>.WithAsyncApi(x => x.WithDescription("..."))</c> on a ChannelBuilder.
/// </summary>
public class AsyncApiChannelOptions
{
    // --- Channel metadata ---

    public string? Title { get; private set; }
    public string? Summary { get; private set; }
    public string? Description { get; private set; }

    // --- Channel-level operation (opt-in grouping) ---

    /// <summary>
    /// When set, all messages on this channel are grouped into a single operation.
    /// When not set, each message gets its own operation (default).
    /// </summary>
    public AsyncApiOperationOptions? Operation { get; private set; }

    public AsyncApiChannelOptions WithTitle(string title)
    {
        Title = title;
        return this;
    }

    public AsyncApiChannelOptions WithSummary(string summary)
    {
        Summary = summary;
        return this;
    }

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
        Operation = new AsyncApiOperationOptions();
        configure(Operation);
        return this;
    }
}
