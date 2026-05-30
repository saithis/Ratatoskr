namespace Ratatoskr.AsyncApi.Model;

/// <summary>
/// Indicates whether a message represents a domain event or a command.
/// </summary>
public enum EventCatalogMessageType
{
    /// <summary>A domain event representing something that happened.</summary>
    Event = 0,

    /// <summary>A command requesting that an action be performed.</summary>
    Command = 1,
}
