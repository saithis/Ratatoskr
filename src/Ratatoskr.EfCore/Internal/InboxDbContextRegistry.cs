namespace Ratatoskr.EfCore.Internal;

/// <summary>
/// Per-DbContext registry that stores inbox options and processor triggers.
/// Populated at startup; used at runtime by inbox processors and
/// <see cref="OutboxTriggerInterceptor{TDbContext}"/>.
/// </summary>
internal class InboxDbContextRegistry
{
    private readonly Dictionary<Type, InboxOptions> _options = new();
    private readonly Dictionary<Type, IProcessorTrigger> _triggers = new();

    // --- Options ---

    /// <summary>Registers options for a specific DbContext type.</summary>
    public void RegisterOptions(Type dbContextType, InboxOptions options) =>
        _options[dbContextType] = options;

    /// <summary>
    /// Returns the options for a DbContext type, or a new default instance if not explicitly configured.
    /// </summary>
    public InboxOptions GetOptions(Type dbContextType) =>
        _options.GetValueOrDefault(dbContextType) ?? new InboxOptions();

    /// <summary>Returns true if options have been explicitly registered for the given type.</summary>
    public bool ContainsOptions(Type dbContextType) =>
        _options.ContainsKey(dbContextType);

    // --- Triggers ---

    /// <summary>Registers a trigger for a specific DbContext type.</summary>
    public void RegisterTrigger(Type dbContextType, IProcessorTrigger trigger) =>
        _triggers[dbContextType] = trigger;

    /// <summary>Returns the trigger for a DbContext type, or null if not registered.</summary>
    public IProcessorTrigger? GetTrigger(Type dbContextType) =>
        _triggers.GetValueOrDefault(dbContextType);
}
