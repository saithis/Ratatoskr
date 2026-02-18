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

    /// <summary>
    /// EventCatalog role override (x-eventcatalog-role).
    /// When not set, derived from the channel type (publish → provider, consume → client).
    /// </summary>
    public EventCatalogRole? Role { get; private set; }

    /// <summary>
    /// EventCatalog message type (x-eventcatalog-message-type).
    /// Defaults to Event.
    /// </summary>
    public EventCatalogMessageType? MessageType { get; private set; }

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

    public AsyncApiMessageOptions WithRole(EventCatalogRole role)
    {
        Role = role;
        return this;
    }

    public AsyncApiMessageOptions WithMessageType(EventCatalogMessageType messageType)
    {
        MessageType = messageType;
        return this;
    }
}

public enum EventCatalogRole
{
    Provider,
    Client,
}

public enum EventCatalogMessageType
{
    Event,
    Command,
    Query,
}
