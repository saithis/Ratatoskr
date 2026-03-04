namespace Ratatoskr.EfCore.Internal;

/// <summary>
/// Unified routing table that maps consume channels to DbContext types and tracks
/// which (channel, wireTypeName) pairs are inbox-managed.
/// Populated at startup from consume channel configuration; used at runtime by
/// <see cref="CompositeInboxRouteInterceptor"/>, <see cref="InboxAcceptor{TDbContext}"/>,
/// and <see cref="OutboxTriggerInterceptor{TDbContext}"/>.
/// </summary>
internal class InboxRoutingTable
{
    private readonly Dictionary<string, Type> _channelDbContextMap = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _inboxMessages = new(StringComparer.Ordinal);

    // --- Channel-to-DbContext mapping ---

    /// <summary>Registers a channel as using a specific DbContext type for inbox.</summary>
    public void RegisterChannel(string channelName, Type dbContextType) =>
        _channelDbContextMap[channelName] = dbContextType;

    /// <summary>Returns the DbContext type for a channel, or null if not inbox-managed.</summary>
    public Type? GetDbContextType(string channelName) =>
        _channelDbContextMap.GetValueOrDefault(channelName);

    /// <summary>Returns all registered channel-to-DbContext mappings.</summary>
    public IEnumerable<(string ChannelName, Type DbContextType)> GetAllChannelMappings() =>
        _channelDbContextMap.Select(kvp => (kvp.Key, kvp.Value));

    // --- Message routing ---

    /// <summary>Registers a message type as inbox-managed on a specific consume channel.</summary>
    public void RegisterMessage(string channelName, string wireTypeName)
    {
        if (!_inboxMessages.TryGetValue(channelName, out var types))
        {
            types = new HashSet<string>(StringComparer.Ordinal);
            _inboxMessages[channelName] = types;
        }

        types.Add(wireTypeName);
    }

    /// <summary>Returns true if the given wire type name is inbox-managed on the specified channel.</summary>
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
