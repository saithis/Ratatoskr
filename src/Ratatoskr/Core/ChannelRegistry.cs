namespace Ratatoskr.Core;

/// <summary>
/// Maintains all registered publish and consume channels and provides lookup methods for routing.
/// </summary>
public sealed class ChannelRegistry
{
    private readonly Dictionary<string, ChannelRegistration> _publishChannels = new(
        StringComparer.Ordinal
    );
    private readonly Dictionary<string, ChannelRegistration> _consumeChannels = new(
        StringComparer.Ordinal
    );

    /// <summary>
    /// O(1) lookup indexes, built by <see cref="Freeze"/>. Null until the registry is frozen.
    /// </summary>
    private LookupIndexes? _indexes;

    private sealed record LookupIndexes(
        Dictionary<Type, PublishInformation> PublishByType,
        Dictionary<string, PublishInformation> PublishByTypeName,
        Dictionary<string, List<(ChannelRegistration Channel, MessageRegistration Message)>> ConsumeByTypeName
    );

    /// <summary>Registers a channel. Throws if a channel with the same name is already registered or the registry is frozen.</summary>
    public void Register(ChannelRegistration channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        if (_indexes != null)
        {
            throw new InvalidOperationException("Registry is frozen and cannot be modified.");
        }

        var registry = channel.Intent is ChannelType.EventPublish or ChannelType.CommandPublish
            ? _publishChannels
            : _consumeChannels;
        if (!registry.TryAdd(channel.ChannelName, channel))
        {
            throw new InvalidOperationException(
                $"Channel '{channel.ChannelName}' is already registered."
            );
        }
    }

    /// <summary>Returns the publish channel with the given name, or null if not found.</summary>
    public ChannelRegistration? GetPublishChannel(string channelName)
    {
        _ = _publishChannels.TryGetValue(channelName, out var channel);
        return channel;
    }

    /// <summary>Returns the consume channel with the given name, or null if not found.</summary>
    public ChannelRegistration? GetConsumeChannel(string channelName)
    {
        _ = _consumeChannels.TryGetValue(channelName, out var channel);
        return channel;
    }

    /// <summary>Returns all registered consume channels.</summary>
    public IEnumerable<ChannelRegistration> GetConsumeChannels() => _consumeChannels.Values;

    /// <summary>Returns all registered publish channels.</summary>
    public IEnumerable<ChannelRegistration> GetPublishChannels() => _publishChannels.Values;

    /// <summary>Returns all registered channels (both publish and consume).</summary>
    public IEnumerable<ChannelRegistration> GetAllChannels() =>
        _publishChannels.Values.Concat(_consumeChannels.Values);

    /// <summary>
    /// Freezes the registry, building O(1) lookup indexes and preventing further registrations.
    /// The message type lookups (<see cref="GetPublishInformation"/> and the <c>Find*</c> methods)
    /// are only available once the registry is frozen.
    /// </summary>
    public void Freeze()
    {
        // Build per-channel message lookup indexes
        foreach (var channel in _publishChannels.Values.Concat(_consumeChannels.Values))
        {
            channel.BuildLookups();
        }

        // Build registry-level O(1) publish indexes
        var publishByType = new Dictionary<Type, PublishInformation>();
        var publishByTypeName = new Dictionary<string, PublishInformation>(StringComparer.Ordinal);
        foreach (var channel in _publishChannels.Values)
        {
            foreach (var msg in channel.Messages)
            {
                var info = new PublishInformation { Channel = channel, Message = msg };
                _ = publishByType.TryAdd(msg.MessageType, info);
                _ = publishByTypeName.TryAdd(msg.MessageTypeName, info);
            }
        }

        // Build registry-level O(1) consume-by-type-name index
        var consumeByTypeName = new Dictionary<
            string,
            List<(ChannelRegistration, MessageRegistration)>
        >(StringComparer.Ordinal);
        foreach (var channel in _consumeChannels.Values)
        {
            foreach (var msg in channel.Messages)
            {
                if (!consumeByTypeName.TryGetValue(msg.MessageTypeName, out var list))
                {
                    list = [];
                    consumeByTypeName[msg.MessageTypeName] = list;
                }
                list.Add((channel, msg));
            }
        }

        _indexes = new LookupIndexes(publishByType, publishByTypeName, consumeByTypeName);
    }

    /// <summary>Finds the publish channel that has a message registered for the given CLR type.</summary>
    public ChannelRegistration? FindPublishChannelForMessage(Type messageType) =>
        GetPublishInformation(messageType)?.Channel;

    /// <summary>Finds the publish channel that has a message registered for the given type name.</summary>
    public ChannelRegistration? FindPublishChannelForTypeName(string messageTypeName) =>
        GetIndexes().PublishByTypeName.GetValueOrDefault(messageTypeName)?.Channel;

    /// <summary>Returns all consume channels that have a message registered for the given type name.</summary>
    public IEnumerable<(
        ChannelRegistration Channel,
        MessageRegistration Message
    )> FindConsumeChannelsForType(string typeName) =>
        GetIndexes().ConsumeByTypeName.GetValueOrDefault(typeName)
        ?? (IEnumerable<(ChannelRegistration, MessageRegistration)>)[];

    /// <summary>Returns combined channel and message registration for publishing the given CLR type, or null if not registered.</summary>
    public PublishInformation? GetPublishInformation(Type messageType) =>
        GetIndexes().PublishByType.GetValueOrDefault(messageType);

    private LookupIndexes GetIndexes() =>
        _indexes
        ?? throw new InvalidOperationException(
            "The channel registry must be frozen before message types can be looked up. Call Freeze() first."
        );
}

/// <summary>
/// Combines a publish channel and its specific message registration for a single publish operation.
/// </summary>
public sealed class PublishInformation
{
    /// <summary>The publish channel the message will be sent to.</summary>
    public required ChannelRegistration Channel { get; init; }

    /// <summary>The message registration describing the message type and its metadata.</summary>
    public required MessageRegistration Message { get; init; }
}
