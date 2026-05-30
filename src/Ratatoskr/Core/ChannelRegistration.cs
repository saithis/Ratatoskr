using System.Collections.ObjectModel;

namespace Ratatoskr.Core;

/// <summary>
/// Holds all configuration for a single registered channel including intent, transports, and messages.
/// </summary>
public sealed class ChannelRegistration(string channelName, ChannelType intent)
{
    /// <summary>The logical name of the channel (e.g. topic or queue name).</summary>
    public string ChannelName { get; } = channelName;

    /// <summary>Whether this channel is used for publishing events, commands, or consuming them.</summary>
    public ChannelType Intent { get; } = intent;

    private readonly Dictionary<Type, object> _extensions = new();

    /// <summary>Gets a typed extension object, or null if not set.</summary>
    public T? GetExtension<T>()
        where T : class => _extensions.TryGetValue(typeof(T), out var value) ? (T)value : null;

    /// <summary>Sets a typed extension object.</summary>
    public void SetExtension<T>(T value)
        where T : class => _extensions[typeof(T)] = value;

    /// <summary>Names of the transports that serve this channel.</summary>
    public ISet<string> Transports { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Message types registered on this channel.</summary>
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

    /// <summary>Finds the message registration for the given CLR type, or null if not registered.</summary>
    public MessageRegistration? GetMessage(Type messageType)
    {
        if (MessagesByType.Count > 0)
        {
            return MessagesByType.GetValueOrDefault(messageType);
        }
        return Messages.FirstOrDefault(m => m.MessageType == messageType);
    }

    /// <summary>Finds the message registration for the given message type name, or null if not registered.</summary>
    public MessageRegistration? GetMessage(string messageTypeName)
    {
        if (MessagesByTypeName.Count > 0)
        {
            return MessagesByTypeName.GetValueOrDefault(messageTypeName);
        }
        return Messages.FirstOrDefault(m => m.MessageTypeName == messageTypeName);
    }
}
