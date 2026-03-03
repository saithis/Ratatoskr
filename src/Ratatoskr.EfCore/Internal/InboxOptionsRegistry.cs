namespace Ratatoskr.EfCore.Internal;

/// <summary>
/// Stores <see cref="InboxOptions"/> per DbContext type.
/// When multiple DbContexts are used for the inbox, each can have
/// independent configuration (retry settings, lock name, etc.).
/// </summary>
internal class InboxOptionsRegistry
{
    private readonly Dictionary<Type, InboxOptions> _options = new();

    /// <summary>Registers options for a specific DbContext type.</summary>
    public void Register(Type dbContextType, InboxOptions options) =>
        _options[dbContextType] = options;

    /// <summary>
    /// Returns the options for a DbContext type, or a new default instance if not explicitly configured.
    /// </summary>
    public InboxOptions Get(Type dbContextType) =>
        _options.GetValueOrDefault(dbContextType) ?? new InboxOptions();

    /// <summary>Returns true if options have been explicitly registered for the given type.</summary>
    public bool Contains(Type dbContextType) =>
        _options.ContainsKey(dbContextType);
}
