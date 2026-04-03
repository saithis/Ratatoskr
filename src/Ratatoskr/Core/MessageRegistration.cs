namespace Ratatoskr.Core;

public class MessageRegistration(Type messageType, string messageTypeName)
{
    public Type MessageType { get; } = messageType;
    public string MessageTypeName { get; internal set; } = messageTypeName;
    public string? DataSchema { get; internal set; }
    public Type? SerializerType { get; internal set; }

    private readonly Dictionary<Type, object> _extensions = new();

    /// <summary>Gets a typed extension object, or null if not set.</summary>
    public T? GetExtension<T>() where T : class =>
        _extensions.TryGetValue(typeof(T), out var value) ? (T)value : null;

    /// <summary>Sets a typed extension object.</summary>
    public void SetExtension<T>(T value) where T : class =>
        _extensions[typeof(T)] = value;
}
