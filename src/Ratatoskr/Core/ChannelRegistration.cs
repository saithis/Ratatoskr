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
    /// O(1) lookup indexes, built by <see cref="ChannelRegistry.Freeze"/>. Null until then.
    /// </summary>
    private Dictionary<Type, MessageRegistration>? _messagesByType;
    private Dictionary<string, MessageRegistration>? _messagesByTypeName;

    internal void BuildLookups()
    {
        var byType = new Dictionary<Type, MessageRegistration>();
        var byTypeName = new Dictionary<string, MessageRegistration>(StringComparer.Ordinal);
        foreach (var msg in Messages)
        {
            byType[msg.MessageType] = msg;
            byTypeName[msg.MessageTypeName] = msg;
        }

        _messagesByType = byType;
        _messagesByTypeName = byTypeName;
    }

    /// <summary>
    /// Finds the message registration for the given CLR type, or null if not registered.
    /// Only available once the owning <see cref="ChannelRegistry"/> is frozen.
    /// </summary>
    public MessageRegistration? GetMessage(Type messageType) =>
        (_messagesByType ?? throw NotFrozen()).GetValueOrDefault(messageType);

    /// <summary>
    /// Finds the message registration for the given message type name, or null if not registered.
    /// Only available once the owning <see cref="ChannelRegistry"/> is frozen.
    /// </summary>
    public MessageRegistration? GetMessage(string messageTypeName) =>
        (_messagesByTypeName ?? throw NotFrozen()).GetValueOrDefault(messageTypeName);

    private InvalidOperationException NotFrozen() =>
        new(
            $"Messages of channel '{ChannelName}' can only be looked up once the channel registry is frozen. Call ChannelRegistry.Freeze() first."
        );
}
