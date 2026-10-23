using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Ratatoskr.Core;

namespace Ratatoskr.EfCore.Internal;

internal class OutboxMessageEntity : BaseMessageEntity
{
    public Guid Id { get; private set; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? ProcessedAt { get; private set; }

    public short ErrorCount { get; private set; }

    [MaxLength(2000)]
    public string Error { get; private set; } = string.Empty;

    public DateTimeOffset? FailedAt { get; private set; }

    /// <summary>
    /// When the message should next be attempted. Null means ready to process.
    /// Used for exponential backoff.
    /// </summary>
    public DateTimeOffset? NextAttemptAt { get; private set; }

    /// <summary>
    /// True if the message has permanently failed and should not be retried.
    /// </summary>
    public bool IsPoisoned { get; private set; }

    /// <summary>
    /// When this message was picked up for processing. Used to detect stuck messages.
    /// </summary>
    public DateTimeOffset? ProcessingStartedAt { get; private set; }

    /// <summary>
    /// Delivery timestamp when this message should be processed/delivered.
    /// Null indicates immediate delivery.
    /// </summary>
    public DateTimeOffset? ScheduledAt { get; private set; }

    /// <summary>
    /// Optimistic concurrency token. Incremented on every state mutation to prevent
    /// two concurrent processors from processing the same message.
    /// </summary>
    public uint Version { get; private set; }

    /// <summary>
    /// Counts how many times this message has been requeued via the management API.
    /// </summary>
    public int RequeuedCount { get; private set; }

    private OutboxMessageEntity() { }

    public static OutboxMessageEntity Create(
        byte[] message,
        MessageProperties props,
        TimeProvider timeProvider,
        string transportName,
        DateTimeOffset? scheduledAt = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transportName);
        if (transportName.Length > 50)
        {
            throw new ArgumentOutOfRangeException(
                nameof(transportName),
                "TransportName must be 50 characters or fewer."
            );
        }

        return new OutboxMessageEntity
        {
            Id = Guid.CreateVersion7(),
            SerializedProperties = JsonSerializer.Serialize(props),
            Content = message,
            CreatedAt = timeProvider.GetUtcNow(),
            TransportName = transportName,
            ScheduledAt = scheduledAt ?? props.ScheduledAt,
        };
    }

    public void MarkAsProcessing(TimeProvider timeProvider)
    {
        ProcessingStartedAt = timeProvider.GetUtcNow();
        Version++;
    }

    public void MarkAsProcessed(TimeProvider timeProvider)
    {
        ProcessedAt = timeProvider.GetUtcNow();
        ProcessingStartedAt = null; // Clear processing flag
        Version++;
    }

    public void PublishFailed(
        string error,
        TimeProvider timeProvider,
        int maxRetries,
        TimeSpan maxRetryDelay
    )
    {
        ErrorCount++;
        Error = error.Length > 2000 ? error[..2000] : error;
        FailedAt = timeProvider.GetUtcNow();
        ProcessingStartedAt = null; // Clear processing flag on failure
        Version++;

        if (ErrorCount >= maxRetries)
        {
            IsPoisoned = true;
            NextAttemptAt = null;
        }
        else
        {
            NextAttemptAt = timeProvider
                .GetUtcNow()
                .Add(BackoffCalculator.CalculateDelay(ErrorCount, maxRetryDelay));
        }
    }

    /// <summary>
    /// Clears the poisoned state so the outbox processor will retry the message.
    /// Increments <see cref="RequeuedCount"/> and resets the error counters.
    /// </summary>
    public void Requeue()
    {
        IsPoisoned = false;
        ErrorCount = 0;
        Error = string.Empty;
        NextAttemptAt = null;
        ProcessingStartedAt = null;
        RequeuedCount++;
        Version++;
    }

    public void MarkAsPoisoned(string reason, TimeProvider timeProvider)
    {
        IsPoisoned = true;
        Error = reason.Length > 2000 ? reason[..2000] : reason;
        FailedAt = timeProvider.GetUtcNow();
        NextAttemptAt = null;
        Version++;
    }
}
