namespace Ratatoskr.EfCore;

/// <summary>
/// Extension data attached to a <see cref="Ratatoskr.Core.MessageRegistration"/>
/// to control whether a message type is inbox-managed on its consume channel.
/// </summary>
internal class InboxMessageOptions
{
    /// <summary>Whether this message type uses the inbox on its consume channel.</summary>
    internal bool UseInbox { get; init; }
}
