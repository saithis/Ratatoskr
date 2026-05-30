namespace Ratatoskr.Core;

/// <summary>
/// Holds configuration for a single message type registered on a channel.
/// </summary>
public sealed class MessageRegistration(Type messageType, string messageTypeName)
{
    /// <summary>The CLR type representing this message.</summary>
    public Type MessageType { get; } = messageType;

    /// <summary>The CloudEvents type identifier (e.g. the class name or value from <see cref="RatatoskrMessageAttribute"/>).</summary>
    public string MessageTypeName { get; internal set; } = messageTypeName;

    /// <summary>Optional URI identifying the schema the message data adheres to.</summary>
    public string? DataSchema { get; internal set; }

    /// <summary>Optional custom serializer type to use for this message.</summary>
    public Type? SerializerType { get; internal set; }

    private readonly Dictionary<Type, object> _extensions = new();

    /// <summary>Gets a typed extension object, or null if not set.</summary>
    public T? GetExtension<T>()
        where T : class => _extensions.TryGetValue(typeof(T), out var value) ? (T)value : null;

    /// <summary>Sets a typed extension object.</summary>
    public void SetExtension<T>(T value)
        where T : class => _extensions[typeof(T)] = value;
}
