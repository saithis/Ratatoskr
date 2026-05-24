using Ratatoskr.Config;

namespace Ratatoskr.Core;

/// <summary>
/// Immutable registry of channel-scoped handler registrations, built at startup from channel configuration.
/// Replaces DI-based handler discovery with explicit lookups by channel name and message type.
/// </summary>
public sealed class ChannelHandlerRegistry
{
    private readonly Dictionary<
        (string ChannelName, Type MessageType),
        ChannelHandlerRegistration[]
    > _fireAndForget = new();
    private readonly Dictionary<
        (string ChannelName, Type MessageType),
        ChannelHandlerRegistration[]
    > _inbox = new();
    private readonly Dictionary<string, ChannelHandlerRegistration[]> _inboxByChannel = new();
    private readonly Dictionary<string, ChannelHandlerRegistration> _inboxByKey = new();

    private ChannelHandlerRegistry() { }

    /// <summary>
    /// Builds the registry from all consume channels in the given <see cref="ChannelRegistry"/>.
    /// </summary>
    public static ChannelHandlerRegistry Build(ChannelRegistry channelRegistry)
    {
        ArgumentNullException.ThrowIfNull(channelRegistry);

        var fireAndForget = new Dictionary<(string, Type), List<ChannelHandlerRegistration>>();
        var inbox = new Dictionary<(string, Type), List<ChannelHandlerRegistration>>();
        var inboxByChannel = new Dictionary<string, List<ChannelHandlerRegistration>>();
        var inboxByKey = new Dictionary<string, ChannelHandlerRegistration>();

        foreach (var channel in channelRegistry.GetConsumeChannels())
        {
            foreach (var message in channel.Messages)
            {
                var handlerRegs = message.GetExtension<MessageHandlerRegistrations>();
                if (handlerRegs == null)
                {
                    continue;
                }

                foreach (var handler in handlerRegs.Handlers)
                {
                    if (handler.IsInbox)
                    {
                        AddToList(inbox, (channel.ChannelName, handler.MessageType), handler);
                        AddToList(inboxByChannel, channel.ChannelName, handler);

                        if (handler.InboxKey != null)
                        {
                            ValidateAndAddKey(
                                inboxByKey,
                                handler.InboxKey,
                                handler,
                                channel.ChannelName
                            );

                            foreach (var legacyKey in handler.LegacyKeys)
                            {
                                ValidateAndAddKey(
                                    inboxByKey,
                                    legacyKey,
                                    handler,
                                    channel.ChannelName
                                );
                            }
                        }
                    }
                    else
                    {
                        AddToList(
                            fireAndForget,
                            (channel.ChannelName, handler.MessageType),
                            handler
                        );
                    }
                }
            }
        }

        var registry = new ChannelHandlerRegistry();

        foreach (var (key, list) in fireAndForget)
        {
            registry._fireAndForget[key] = [.. list];
        }
        foreach (var (key, list) in inbox)
        {
            registry._inbox[key] = [.. list];
        }
        foreach (var (key, list) in inboxByChannel)
        {
            registry._inboxByChannel[key] = [.. list];
        }
        foreach (var (key, value) in inboxByKey)
        {
            registry._inboxByKey[key] = value;
        }

        return registry;
    }

    private static void ValidateAndAddKey(
        Dictionary<string, ChannelHandlerRegistration> dict,
        string key,
        ChannelHandlerRegistration handler,
        string channelName
    )
    {
        if (dict.TryGetValue(key, out var existing))
        {
            throw new InvalidOperationException(
                $"Duplicate inbox handler key '{key}' registered on channel '{channelName}' "
                    + $"for handler '{handler.HandlerType.Name}'. "
                    + $"Key is already used by handler '{existing.HandlerType.Name}'. "
                    + "Inbox handler keys must be globally unique because the inbox processor "
                    + "looks up handlers by key across all channels and DbContexts."
            );
        }

        dict[key] = handler;
    }

    private static void AddToList<TKey>(
        Dictionary<TKey, List<ChannelHandlerRegistration>> dict,
        TKey key,
        ChannelHandlerRegistration handler
    )
        where TKey : notnull
    {
        if (!dict.TryGetValue(key, out var list))
        {
            list = new List<ChannelHandlerRegistration>();
            dict[key] = list;
        }
        list.Add(handler);
    }

    /// <summary>
    /// Returns fire-and-forget handler types for a specific channel and message type.
    /// </summary>
    public IReadOnlyList<ChannelHandlerRegistration> GetFireAndForgetHandlers(
        string channelName,
        Type messageType
    ) =>
        _fireAndForget.TryGetValue((channelName, messageType), out var list)
            ? list
            : Array.Empty<ChannelHandlerRegistration>();

    /// <summary>
    /// Returns inbox handler registrations for a specific channel.
    /// </summary>
    public IReadOnlyList<ChannelHandlerRegistration> GetInboxHandlers(string channelName) =>
        _inboxByChannel.TryGetValue(channelName, out var list)
            ? list
            : Array.Empty<ChannelHandlerRegistration>();

    /// <summary>
    /// Returns inbox handler registrations for a specific channel and message type.
    /// </summary>
    public IReadOnlyList<ChannelHandlerRegistration> GetInboxHandlers(
        string channelName,
        Type messageType
    ) =>
        _inbox.TryGetValue((channelName, messageType), out var list)
            ? list
            : Array.Empty<ChannelHandlerRegistration>();

    /// <summary>
    /// Looks up an inbox handler registration by its stable key or a legacy key.
    /// </summary>
    public ChannelHandlerRegistration? GetInboxRegistrationByKey(string key) =>
        _inboxByKey.GetValueOrDefault(key);

    /// <summary>
    /// Returns all inbox handler registrations across all channels.
    /// Legacy key aliases are excluded — each handler appears exactly once (by primary key).
    /// </summary>
    public IReadOnlyCollection<ChannelHandlerRegistration> GetAllInboxHandlers() =>
        [.. _inboxByKey.Values.Distinct()];

    /// <summary>True if no inbox handlers have been registered.</summary>
    public bool HasNoInboxHandlers => _inboxByKey.Count == 0;
}
