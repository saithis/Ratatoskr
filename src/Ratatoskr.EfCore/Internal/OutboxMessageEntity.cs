using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Ratatoskr.Core;

namespace Ratatoskr.EfCore.Internal;

internal class OutboxMessageEntity
{
    public Guid Id { get; private set; }
    
    public required byte[] Content { get; init; }

    /// <summary>
    /// JSON serialized properties
    /// </summary>
    public required string SerializedProperties { get; init; }
    
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
    /// The transport this outbox entry targets (e.g. "rabbitmq", "local").
    /// </summary>
    [MaxLength(50)]
    public string TransportName { get; private set; } = string.Empty;

    /// <summary>
    /// The full type name of the DbContext that created this outbox message.
    /// Used to scope cleanup when multiple DbContexts share the same physical database.
    /// Empty string for legacy rows (pre-migration).
    /// </summary>
    [MaxLength(500)]
    public string SourceContext { get; private set; } = string.Empty;

    public MessageProperties GetProperties() => 
        JsonSerializer.Deserialize<MessageProperties>(SerializedProperties)
        ?? throw new OutboxMessageSerializationException("Could not deserialize the message properties.", SerializedProperties);

    private OutboxMessageEntity(){}
    public static OutboxMessageEntity Create(byte[] message, MessageProperties props, TimeProvider timeProvider, string transportName, string sourceContext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transportName);
        if (transportName.Length > 50)
            throw new ArgumentOutOfRangeException(nameof(transportName), "TransportName must be 50 characters or fewer.");
        return new OutboxMessageEntity
        {
            Id = Guid.CreateVersion7(),
            SerializedProperties = JsonSerializer.Serialize(props),
            Content = message,
            CreatedAt = timeProvider.GetUtcNow(),
            TransportName = transportName,
            SourceContext = sourceContext,
        };
    }

    public void MarkAsProcessing(TimeProvider timeProvider)
    {
        ProcessingStartedAt = timeProvider.GetUtcNow();
    }
    
    public void MarkAsProcessed(TimeProvider timeProvider)
    {
        ProcessedAt = timeProvider.GetUtcNow();
        ProcessingStartedAt = null; // Clear processing flag
    }

    public void PublishFailed(string error, TimeProvider timeProvider, int maxRetries, TimeSpan maxRetryDelay)
    {
        ErrorCount++;
        Error = error.Length > 2000 ? error[..2000] : error;
        FailedAt = timeProvider.GetUtcNow();
        ProcessingStartedAt = null; // Clear processing flag on failure
        
        if (ErrorCount >= maxRetries)
        {
            IsPoisoned = true;
            NextAttemptAt = null;
        }
        else
        {
            // Exponential backoff with equal jitter: base/2 + random(0, base/2)
            // Prevents thundering herd while maintaining a predictable minimum delay
            var baseDelay = Math.Min(Math.Pow(2, ErrorCount), maxRetryDelay.TotalSeconds);
            var delaySeconds = baseDelay * 0.5 + baseDelay * 0.5 * Random.Shared.NextDouble();
            NextAttemptAt = timeProvider.GetUtcNow().AddSeconds(delaySeconds);
        }
    }
    
    public void MarkAsPoisoned(string reason, TimeProvider timeProvider)
    {
        IsPoisoned = true;
        Error = reason.Length > 2000 ? reason[..2000] : reason;
        FailedAt = timeProvider.GetUtcNow();
        NextAttemptAt = null;
    }
}
