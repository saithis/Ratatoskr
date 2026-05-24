using System.Collections.ObjectModel;

namespace Ratatoskr.Core;

public sealed class ChannelRegistration(string channelName, ChannelType intent)
{
    public string ChannelName { get; } = channelName;
    public ChannelType Intent { get; } = intent;

    private readonly Dictionary<Type, object> _extensions = new();

    /// <summary>Gets a typed extension object, or null if not set.</summary>
    public T? GetExtension<T>()
        where T : class => _extensions.TryGetValue(typeof(T), out var value) ? (T)value : null;

    /// <summary>Sets a typed extension object.</summary>
    public void SetExtension<T>(T value)
        where T : class => _extensions[typeof(T)] = value;

    public ISet<string> Transports { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public IList<MessageRegistration> Messages { get; } = new Collection<MessageRegistration>();

    /// <summary>
    /// O(1) lookup indexes — populated by ChannelRegistry.Freeze()
    /// </summary>
    internal Dictionary<Type, MessageRegistration> MessagesByType { get; } = new();
    internal Dictionary<string, MessageRegistration> MessagesByTypeName { get; } =
        new(StringComparer.Ordinal);

    internal void BuildLookups()
    {
        MessagesByType.Clear();
        MessagesByTypeName.Clear();
        foreach (var msg in Messages)
        {
            MessagesByType[msg.MessageType] = msg;
            MessagesByTypeName[msg.MessageTypeName] = msg;
        }
    }

    public MessageRegistration? GetMessage(Type messageType)
    {
        if (MessagesByType.Count > 0)
        {
            return MessagesByType.GetValueOrDefault(messageType);
        }
        return Messages.FirstOrDefault(m => m.MessageType == messageType);
    }

    public MessageRegistration? GetMessage(string messageTypeName)
    {
        if (MessagesByTypeName.Count > 0)
        {
            return MessagesByTypeName.GetValueOrDefault(messageTypeName);
        }
        return Messages.FirstOrDefault(m => m.MessageTypeName == messageTypeName);
    }
}
