namespace Ratatoskr.AsyncApi.Config;

/// <summary>
/// AsyncAPI documentation options for a channel and its operation.
/// Configure via <c>.WithAsyncApi(x => x.WithDescription("..."))</c> on a ChannelBuilder.
/// </summary>
public class AsyncApiChannelOptions
{
    // --- Channel metadata ---

    public string? Title { get; private set; }
    public string? Summary { get; private set; }
    public string? Description { get; private set; }

    // --- Operation metadata ---

    /// <summary>Custom identifier for the AsyncAPI operation. Defaults to the channel name.</summary>
    public string? OperationId { get; private set; }
    public string? OperationTitle { get; private set; }
    public string? OperationSummary { get; private set; }
    public string? OperationDescription { get; private set; }

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

    public AsyncApiChannelOptions WithOperationId(string operationId)
    {
        OperationId = operationId;
        return this;
    }

    public AsyncApiChannelOptions WithOperationTitle(string title)
    {
        OperationTitle = title;
        return this;
    }

    public AsyncApiChannelOptions WithOperationSummary(string summary)
    {
        OperationSummary = summary;
        return this;
    }

    public AsyncApiChannelOptions WithOperationDescription(string description)
    {
        OperationDescription = description;
        return this;
    }
}
