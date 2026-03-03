namespace Ratatoskr.EfCore.Internal;

/// <summary>
/// Maps DbContext types to their corresponding <see cref="IProcessorTrigger"/>.
/// Used by <see cref="OutboxTriggerInterceptor{TDbContext}"/> to trigger the
/// correct inbox processor when inbox entries are written in an outbox transaction.
/// </summary>
internal class InboxProcessorTriggerRegistry
{
    private readonly Dictionary<Type, IProcessorTrigger> _triggers = new();

    /// <summary>Registers a trigger for a specific DbContext type.</summary>
    public void Register(Type dbContextType, IProcessorTrigger trigger) =>
        _triggers[dbContextType] = trigger;

    /// <summary>Returns the trigger for a DbContext type, or null if not registered.</summary>
    public IProcessorTrigger? Get(Type dbContextType) =>
        _triggers.GetValueOrDefault(dbContextType);
}
