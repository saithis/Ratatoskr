namespace Ratatoskr.EfCore;

/// <summary>
/// Interface that DbContext classes implement to support Ratatoskr durability (both outbox and inbox).
/// </summary>
public interface IRatatoskrDbContext
{
    /// <summary>
    /// Collection for staging messages to be sent via the outbox when SaveChangesAsync is called.
    /// </summary>
    public OutboxStagingCollection Outbox { get; }
}
