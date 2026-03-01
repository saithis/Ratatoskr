namespace Ratatoskr.Core;

/// <summary>
/// In-memory registry of handlers with stable keys for inbox-based durable delivery.
/// Populated at startup; used at runtime by <see cref="MessageDispatcher"/> and the inbox processor.
/// </summary>
public class InboxHandlerRegistry
{
    private readonly Dictionary<string, InboxHandlerRegistration> _byKey = new();
    private readonly Dictionary<Type, List<InboxHandlerRegistration>> _byMessageType = new();
    private readonly Dictionary<string, List<InboxHandlerRegistration>> _byWireTypeName = new();
    private readonly Dictionary<Type, InboxHandlerRegistration> _byHandlerType = new();

    internal void Register(string key, Type messageType, Type handlerType, string? wireTypeName)
    {
        if (_byKey.TryGetValue(key, out var existing))
            throw new InvalidOperationException(
                $"Duplicate inbox handler key '{key}': already registered for handler '{existing.HandlerType.Name}', " +
                $"cannot register again for handler '{handlerType.Name}'. Each handler must have a unique stable key.");

        var registration = new InboxHandlerRegistration(key, messageType, handlerType, wireTypeName);

        _byKey[key] = registration;
        _byHandlerType[handlerType] = registration;

        if (!_byMessageType.TryGetValue(messageType, out var byTypeList))
        {
            byTypeList = new List<InboxHandlerRegistration>();
            _byMessageType[messageType] = byTypeList;
        }
        byTypeList.Add(registration);

        if (wireTypeName != null)
        {
            if (!_byWireTypeName.TryGetValue(wireTypeName, out var byWireList))
            {
                byWireList = new List<InboxHandlerRegistration>();
                _byWireTypeName[wireTypeName] = byWireList;
            }
            byWireList.Add(registration);
        }
    }

    /// <summary>
    /// Looks up a registration by its stable handler key.
    /// Returns null if the key is not registered (e.g. handler was removed after messages were persisted).
    /// </summary>
    public InboxHandlerRegistration? GetByKey(string key) =>
        _byKey.GetValueOrDefault(key);

    /// <summary>Returns all inbox-managed handlers for a given CLR message type.</summary>
    public IReadOnlyList<InboxHandlerRegistration> GetByMessageType(Type messageType) =>
        _byMessageType.TryGetValue(messageType, out var list) ? list : Array.Empty<InboxHandlerRegistration>();

    /// <summary>Returns all inbox-managed handlers for a given wire type name (e.g. "order.created").</summary>
    public IReadOnlyList<InboxHandlerRegistration> GetByWireTypeName(string wireTypeName) =>
        _byWireTypeName.TryGetValue(wireTypeName, out var list) ? list : Array.Empty<InboxHandlerRegistration>();

    /// <summary>
    /// Returns the inbox registration for a given handler CLR type, or null if the handler has no inbox key.
    /// Used by <see cref="MessageDispatcher"/> to detect which handlers to skip during synchronous dispatch.
    /// </summary>
    public InboxHandlerRegistration? GetByHandlerType(Type handlerType) =>
        _byHandlerType.GetValueOrDefault(handlerType);

    /// <summary>True if no handlers have been registered with inbox keys.</summary>
    public bool IsEmpty => _byKey.Count == 0;

    /// <summary>Returns all registered inbox handler registrations.</summary>
    public IReadOnlyCollection<InboxHandlerRegistration> GetAll() => _byKey.Values;
}
