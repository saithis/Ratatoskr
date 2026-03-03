namespace Ratatoskr.Core;

public class HandlerRegistration(Type handlerType, Type messageType, string? key)
{
    private readonly Dictionary<Type, object> _extensions = new();

    /// <summary>
    /// Stable handler key set via <c>AddHandler</c> or <see cref="HandlerKeyAttribute"/>.
    /// Priority: AddHandler parameter &gt; <see cref="HandlerKeyAttribute"/> &gt; <c>null</c>.
    /// </summary>
    internal string? Key { get; init; } = key;

    internal Type MessageType { get; init; } = messageType;
    internal Type HandlerType { get; init; } = handlerType;

    /// <summary>Gets a typed extension object, or null if not set.</summary>
    public T? GetExtension<T>() where T : class =>
        _extensions.TryGetValue(typeof(T), out var value) ? (T)value : null;

    /// <summary>Sets a typed extension object.</summary>
    public void SetExtension<T>(T value) where T : class =>
        _extensions[typeof(T)] = value;
}
