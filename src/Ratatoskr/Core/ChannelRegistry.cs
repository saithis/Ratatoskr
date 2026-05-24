namespace Ratatoskr.Core;

public sealed class ChannelRegistry
{
    private readonly Dictionary<string, ChannelRegistration> _publishChannels = new();
    private readonly Dictionary<string, ChannelRegistration> _consumeChannels = new();
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

    public ChannelRegistration? GetPublishChannel(string channelName)
    {
        _ = _publishChannels.TryGetValue(channelName, out var channel);
        return channel;
    }

    public ChannelRegistration? GetConsumeChannel(string channelName)
    {
        _ = _consumeChannels.TryGetValue(channelName, out var channel);
        return channel;
    }

    public IEnumerable<ChannelRegistration> GetConsumeChannels() => _consumeChannels.Values;

    public IEnumerable<ChannelRegistration> GetPublishChannels() => _publishChannels.Values;

    public IEnumerable<ChannelRegistration> GetAllChannels() =>
        _publishChannels.Values.Concat(_consumeChannels.Values);

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

public sealed class PublishInformation
{
    public required ChannelRegistration Channel { get; init; }
    public required MessageRegistration Message { get; init; }
}
