namespace Ratatoskr.Core;

public class ChannelRegistration(string channelName, ChannelType intent)
{
    public string ChannelName { get; } = channelName;
    public ChannelType Intent { get; } = intent;

    private readonly Dictionary<Type, object> _extensions = new();

    /// <summary>Gets a typed extension object, or null if not set.</summary>
    public T? GetExtension<T>() where T : class =>
        _extensions.TryGetValue(typeof(T), out var value) ? (T)value : null;

    /// <summary>Sets a typed extension object.</summary>
    public void SetExtension<T>(T value) where T : class =>
        _extensions[typeof(T)] = value;

    public HashSet<string> Transports { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<MessageRegistration> Messages { get; } = new();

    public MessageRegistration? GetMessage(Type messageType)
    {
        return Messages.FirstOrDefault(m => m.MessageType == messageType);
    }

    public MessageRegistration? GetMessage(string messageTypeName)
    {
        return Messages.FirstOrDefault(m => m.MessageTypeName == messageTypeName);
    }
}
