using Ratatoskr.Core;

namespace Ratatoskr.EfCore;

/// <summary>
/// Collection for staging messages to be sent via the outbox pattern.
/// Messages added here will be persisted to the database and sent transactionally.
/// </summary>
public sealed class OutboxStagingCollection
{
    internal List<Item> StagedItems { get; } = [];

    /// <summary>
    /// Stages a message to be sent when SaveChanges is called.
    /// The message will be persisted to the outbox table and sent by the background processor.
    /// </summary>
    /// <typeparam name="TMessage">The message type (must be registered in configuration)</typeparam>
    /// <param name="message">The message to send</param>
    /// <param name="properties">Optional message properties</param>
    public void Add<TMessage>(TMessage message, MessageProperties? properties = null)
        where TMessage : notnull
    {
        StagedItems.Add(
            new Item { Message = message, Properties = properties ?? new MessageProperties() }
        );
    }

    /// <summary>
    /// Stages a message to be sent when SaveChanges is called.
    /// </summary>
    public void Add(object message, MessageProperties? properties = null)
    {
        ArgumentNullException.ThrowIfNull(message);

        StagedItems.Add(
            new Item { Message = message, Properties = properties ?? new MessageProperties() }
        );
    }

    /// <summary>
    /// Gets the number of messages currently staged.
    /// </summary>
    public int Count => StagedItems.Count;

    internal void ClearStaged() => StagedItems.Clear();

    internal class Item
    {
        internal required object Message { get; init; }
        internal required MessageProperties Properties { get; init; }
    }
}
