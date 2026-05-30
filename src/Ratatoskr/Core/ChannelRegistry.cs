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
    private bool _frozen;

    /// <summary>
    /// O(1) lookup indexes — populated by Freeze()
    /// </summary>
    private Dictionary<Type, PublishInformation>? _publishByType;
    private Dictionary<string, PublishInformation>? _publishByTypeName;
    private Dictionary<
        string,
        List<(ChannelRegistration Channel, MessageRegistration Message)>
    >? _consumeByTypeName;

    /// <summary>Registers a channel. Throws if a channel with the same name is already registered or the registry is frozen.</summary>
    public void Register(ChannelRegistration channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        if (_frozen)
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

    /// <summary>Freezes the registry, building O(1) lookup indexes and preventing further registrations.</summary>
    public void Freeze()
    {
        _frozen = true;

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
        _publishByType = publishByType;
        _publishByTypeName = publishByTypeName;

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
        _consumeByTypeName = consumeByTypeName;
    }

    /// <summary>Finds the publish channel that has a message registered for the given CLR type.</summary>
    public ChannelRegistration? FindPublishChannelForMessage(Type messageType)
    {
        if (_publishByType != null)
        {
            return _publishByType.GetValueOrDefault(messageType)?.Channel;
        }

        return _publishChannels.Values.FirstOrDefault(c =>
            c.Messages.Any(m => m.MessageType == messageType)
        );
    }

    /// <summary>Finds the publish channel that has a message registered for the given type name.</summary>
    public ChannelRegistration? FindPublishChannelForTypeName(string messageTypeName)
    {
        if (_publishByTypeName != null)
        {
            return _publishByTypeName.GetValueOrDefault(messageTypeName)?.Channel;
        }

        return _publishChannels.Values.FirstOrDefault(c =>
            c.Messages.Any(m => m.MessageTypeName == messageTypeName)
        );
    }

    /// <summary>Returns all consume channels that have a message registered for the given type name.</summary>
    public IEnumerable<(
        ChannelRegistration Channel,
        MessageRegistration Message
    )> FindConsumeChannelsForType(string typeName)
    {
        if (_consumeByTypeName != null)
        {
            return _consumeByTypeName.GetValueOrDefault(typeName)
                ?? (IEnumerable<(ChannelRegistration, MessageRegistration)>)[];
        }

        return FindConsumeChannelsForTypeSlow(typeName);
    }

    private IEnumerable<(
        ChannelRegistration Channel,
        MessageRegistration Message
    )> FindConsumeChannelsForTypeSlow(string typeName)
    {
        foreach (var channel in _consumeChannels.Values)
        {
            var msg = channel.Messages.FirstOrDefault(m => m.MessageTypeName == typeName);
            if (msg != null)
            {
                yield return (channel, msg);
            }
        }
    }

    /// <summary>Returns combined channel and message registration for publishing the given CLR type, or null if not registered.</summary>
    public PublishInformation? GetPublishInformation(Type messageType)
    {
        if (_publishByType != null)
        {
            return _publishByType.GetValueOrDefault(messageType);
        }

        var channel = FindPublishChannelForMessage(messageType);
        var message = channel?.GetMessage(messageType);
        if (channel == null || message == null)
        {
            return null;
        }
        return new PublishInformation { Channel = channel, Message = message };
    }
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
