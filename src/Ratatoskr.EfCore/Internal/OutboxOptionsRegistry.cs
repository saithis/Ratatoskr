namespace Ratatoskr.EfCore.Internal;

/// <summary>
/// Stores <see cref="OutboxOptions"/> per DbContext type.
/// When multiple DbContexts are used for the outbox, each can have
/// independent configuration (retry settings, lock name, etc.).
/// </summary>
internal class OutboxOptionsRegistry
{
    private readonly Dictionary<Type, OutboxOptions> _options = new();

    /// <summary>Registers options for a specific DbContext type.</summary>
    public void Register(Type dbContextType, OutboxOptions options) =>
        _options[dbContextType] = options;

    /// <summary>
    /// Returns the options for a DbContext type, or a new default instance if not explicitly configured.
    /// </summary>
    public OutboxOptions Get(Type dbContextType) =>
        _options.GetValueOrDefault(dbContextType) ?? new OutboxOptions();

    /// <summary>Returns true if options have been explicitly registered for the given type.</summary>
    public bool Contains(Type dbContextType) =>
        _options.ContainsKey(dbContextType);
}
