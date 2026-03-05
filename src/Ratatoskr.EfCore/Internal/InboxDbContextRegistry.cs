namespace Ratatoskr.EfCore.Internal;

/// <summary>
/// Per-DbContext registry that stores inbox options and processor triggers.
/// Populated at startup; used at runtime by inbox processors and
/// <see cref="OutboxTriggerInterceptor{TDbContext}"/>.
/// </summary>
internal class InboxDbContextRegistry
{
    private readonly TypedOptionsRegistry<InboxOptions> _options = new("inbox options");
    private readonly Dictionary<Type, IProcessorTrigger> _triggers = new();

    // --- Options (delegated to TypedOptionsRegistry) ---

    /// <summary>Registers options for a specific DbContext type.</summary>
    public void RegisterOptions(Type dbContextType, InboxOptions options) =>
        _options.Register(dbContextType, options);

    /// <summary>
    /// Returns the options for a DbContext type.
    /// Throws if the type was not registered via <c>UseEfCoreInbox&lt;T&gt;()</c>.
    /// </summary>
    public InboxOptions GetOptions(Type dbContextType) =>
        _options.Get(dbContextType);

    /// <summary>Returns true if options have been explicitly registered for the given type.</summary>
    public bool ContainsOptions(Type dbContextType) =>
        _options.Contains(dbContextType);

    // --- Triggers ---

    /// <summary>Registers a trigger for a specific DbContext type.</summary>
    public void RegisterTrigger(Type dbContextType, IProcessorTrigger trigger) =>
        _triggers[dbContextType] = trigger;

    /// <summary>Returns the trigger for a DbContext type, or null if not registered.</summary>
    public IProcessorTrigger? GetTrigger(Type dbContextType) =>
        _triggers.GetValueOrDefault(dbContextType);
}
