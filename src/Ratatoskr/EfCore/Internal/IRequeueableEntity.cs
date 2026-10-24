namespace Ratatoskr.EfCore.Internal;

/// <summary>
/// A poisoned row (outbox message or inbox handler status) that the management API can requeue.
/// </summary>
internal interface IRequeueableEntity
{
    Guid Id { get; }
    DateTimeOffset CreatedAt { get; }
    bool IsPoisoned { get; }

    /// <summary>Clears the poisoned state so the processor retries the row.</summary>
    void Requeue();
}
