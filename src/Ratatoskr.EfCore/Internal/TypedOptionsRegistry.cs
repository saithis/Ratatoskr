namespace Ratatoskr.EfCore.Internal;

/// <summary>
/// Generic per-DbContext options registry that maps DbContext types to their options.
/// Used by both inbox and outbox subsystems to store per-DbContext configuration.
/// </summary>
internal class TypedOptionsRegistry<TOptions>
{
    private readonly Dictionary<Type, TOptions> _options = new();
    private readonly string _optionsName;

    public TypedOptionsRegistry(string optionsName) => _optionsName = optionsName;

    /// <summary>Registers options for a specific DbContext type.</summary>
    public void Register(Type dbContextType, TOptions options) =>
        _options[dbContextType] = options;

    /// <summary>
    /// Returns the options for a DbContext type.
    /// Throws if the type was not registered.
    /// </summary>
    public TOptions Get(Type dbContextType) =>
        _options.GetValueOrDefault(dbContextType)
        ?? throw new InvalidOperationException(
            $"No {_optionsName} registered for DbContext type '{dbContextType.FullName}'.");

    /// <summary>Returns true if options have been explicitly registered for the given type.</summary>
    public bool Contains(Type dbContextType) =>
        _options.ContainsKey(dbContextType);
}
