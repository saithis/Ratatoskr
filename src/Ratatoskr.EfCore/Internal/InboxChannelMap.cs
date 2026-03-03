namespace Ratatoskr.EfCore.Internal;

/// <summary>
/// Maps consume channel names to the DbContext type used for their inbox.
/// Populated at startup from channel configuration; used at runtime by
/// <see cref="CompositeInboxRouteInterceptor"/> and <see cref="OutboxTriggerInterceptor{TDbContext}"/>.
/// </summary>
internal class InboxChannelMap
{
    private readonly Dictionary<string, Type> _map = new(StringComparer.Ordinal);

    /// <summary>Registers a channel as using a specific DbContext type for inbox.</summary>
    public void Register(string channelName, Type dbContextType) =>
        _map[channelName] = dbContextType;

    /// <summary>Returns the DbContext type for a channel, or null if not inbox-managed.</summary>
    public Type? GetDbContextType(string channelName) =>
        _map.GetValueOrDefault(channelName);

    /// <summary>Returns all registered channel-to-DbContext mappings.</summary>
    public IEnumerable<(string ChannelName, Type DbContextType)> GetAll() =>
        _map.Select(kvp => (kvp.Key, kvp.Value));
}
