namespace Ratatoskr.EfCore.Internal;

/// <summary>
/// Maps (channelName, wireTypeName) to whether the message is inbox-managed.
/// Populated at startup from consume channel configuration.
/// Used at runtime by <see cref="InboxAcceptor{TDbContext}"/> to decide
/// whether a message should be persisted to the inbox.
/// </summary>
internal class InboxMessageRegistry
{
    private readonly Dictionary<string, HashSet<string>> _inboxMessages = new(StringComparer.Ordinal);

    /// <summary>
    /// Registers a message type as inbox-managed on a specific consume channel.
    /// </summary>
    public void Register(string channelName, string wireTypeName)
    {
        if (!_inboxMessages.TryGetValue(channelName, out var types))
        {
            types = new HashSet<string>(StringComparer.Ordinal);
            _inboxMessages[channelName] = types;
        }

        types.Add(wireTypeName);
    }

    /// <summary>
    /// Returns true if the given wire type name is inbox-managed on the specified channel.
    /// </summary>
    public bool IsInboxManaged(string channelName, string wireTypeName) =>
        _inboxMessages.TryGetValue(channelName, out var types) && types.Contains(wireTypeName);

    /// <summary>True if no messages have been registered as inbox-managed.</summary>
    public bool IsEmpty => _inboxMessages.Count == 0;

    /// <summary>Returns all channel names that have inbox-managed messages.</summary>
    public IEnumerable<string> GetChannelNames() => _inboxMessages.Keys;

    /// <summary>Returns all inbox-managed wire type names for a channel.</summary>
    public IReadOnlySet<string> GetWireTypeNames(string channelName) =>
        _inboxMessages.TryGetValue(channelName, out var types) ? types : new HashSet<string>();
}
