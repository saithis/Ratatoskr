using System.ComponentModel.DataAnnotations;

namespace Ratatoskr.EfCore.Internal;

/// <summary>
/// Tracks delivery status for one (message, handler) pair.
/// One row per handler per received message.
/// The combination (MessageId, HandlerKey) has a unique constraint — the deduplication key.
/// </summary>
internal class InboxHandlerStatusEntity
{
    public Guid Id { get; private set; }

    /// <summary>FK to <see cref="InboxMessageEntity.Id"/>.</summary>
    public string MessageId { get; private set; } = string.Empty;

    /// <summary>Stable user-assigned handler key. Persisted as the deduplication and retry key.</summary>
    [MaxLength(200)]
    public string HandlerKey { get; private set; } = string.Empty;

    public int ErrorCount { get; private set; }

    [MaxLength(2000)]
    public string LastError { get; private set; } = string.Empty;

    /// <summary>Set when the processor picks up this status. Used for stuck detection.</summary>
    public DateTimeOffset? ProcessingStartedAt { get; private set; }

    /// <summary>When to retry next. Null means ready to process immediately.</summary>
    public DateTimeOffset? NextAttemptAt { get; private set; }

    /// <summary>True if max retries exceeded. Row is kept for manual retry via future UI.</summary>
    public bool IsPoisoned { get; private set; }

    /// <summary>Set when the handler completes successfully.</summary>
    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>When this handler status row was created.</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// Optimistic concurrency token. Incremented on every state mutation to prevent
    /// two concurrent processors from claiming and processing the same row.
    /// </summary>
    public uint Version { get; private set; }

    private InboxHandlerStatusEntity() { }

    public static InboxHandlerStatusEntity Create(string messageId, string handlerKey, TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(handlerKey);
        return new InboxHandlerStatusEntity
        {
            Id = Guid.CreateVersion7(),
            MessageId = messageId,
            HandlerKey = handlerKey,
            CreatedAt = timeProvider.GetUtcNow(),
        };
    }

    public void MarkAsProcessing(TimeProvider timeProvider)
    {
        ProcessingStartedAt = timeProvider.GetUtcNow();
        Version++;
    }

    public void MarkAsCompleted(TimeProvider timeProvider)
    {
        CompletedAt = timeProvider.GetUtcNow();
        ProcessingStartedAt = null;
        Version++;
    }

    public void MarkAsFailed(string error, TimeProvider timeProvider, int maxRetries, TimeSpan maxRetryDelay)
    {
        ErrorCount++;
        LastError = error.Length > 2000 ? error[..2000] : error;
        ProcessingStartedAt = null;
        Version++;

        if (ErrorCount >= maxRetries)
        {
            IsPoisoned = true;
            NextAttemptAt = null;
        }
        else
        {
            NextAttemptAt = timeProvider.GetUtcNow().Add(BackoffCalculator.CalculateDelay(ErrorCount, maxRetryDelay));
        }
    }

    /// <summary>
    /// Immediately marks this handler status as poisoned without going through the retry cycle.
    /// Used for deterministically unrecoverable errors (e.g. message deleted, handler key removed).
    /// </summary>
    public void MarkAsPoisoned(string error, TimeProvider timeProvider)
    {
        LastError = error.Length > 2000 ? error[..2000] : error;
        ProcessingStartedAt = null;
        IsPoisoned = true;
        NextAttemptAt = null;
        Version++;
    }
}
