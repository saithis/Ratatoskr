namespace Ratatoskr.AsyncApi.Model;

/// <summary>
/// Represents the EventCatalog role for the <c>x-eventcatalog-role</c> extension.
/// </summary>
public enum EventCatalogRole
{
    /// <summary>The service produces/publishes this message.</summary>
    Provider = 0,

    /// <summary>The service consumes/subscribes to this message.</summary>
    Client = 1,
}
