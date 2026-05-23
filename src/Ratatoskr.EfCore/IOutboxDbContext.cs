namespace Ratatoskr.EfCore;

/// <summary>
/// Interface that DbContext classes must implement to support the outbox pattern.
/// </summary>
/// <example>
/// <code>
/// public class MyDbContext : DbContext, IOutboxDbContext
/// {
///     public OutboxStagingCollection OutboxMessages { get; } = new();
///
///     protected override void OnModelCreating(ModelBuilder modelBuilder)
///     {
///         modelBuilder.AddRatatoskrEfCoreModel(Database);
///     }
///
///     // Usage in application code:
///     // db.MyEntities.Add(entity);
///     // db.OutboxMessages.Add(new MyEvent { ... });
///     // await db.SaveChangesAsync(); // Both saved transactionally
/// }
/// </code>
/// </example>
public interface IOutboxDbContext
{
    /// <summary>
    /// Collection for staging messages to be sent via the outbox.
    /// Messages added here will be persisted and sent when SaveChanges is called.
    /// </summary>
    /// <remarks>
    /// This provides transactional message publishing - if the database transaction
    /// fails, the messages won't be sent. For non-transactional publishing,
    /// use <see cref="IRatatoskr.PublishDirectAsync{TMessage}"/> instead.
    /// </remarks>
    public OutboxStagingCollection OutboxMessages { get; }
}
