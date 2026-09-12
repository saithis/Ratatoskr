using System.Text.Json.Serialization;

namespace Ratatoskr.Management.Contracts;

/// <summary>The DbContexts one service exposes.</summary>
public sealed record ContextListResponse(IReadOnlyList<ContextListItem> Contexts);

/// <summary>One DbContext a service exposes.</summary>
public sealed record ContextListItem(string Name, bool HasOutbox, bool HasInbox);

/// <summary>Backlog gauges plus the last time each processor made progress.</summary>
public sealed record ContextHealthResponse(
    string Name,
    long PendingOutbox,
    long PoisonedOutbox,
    long PendingInbox,
    long PoisonedInbox,
    DateTimeOffset? LastOutboxProcessedAt,
    DateTimeOffset? LastInboxProcessedAt
);

/// <summary>How many rows a filter matches.</summary>
public sealed record MessageCountResponse(long Count);

/// <summary>CloudEvents metadata for one message, flattened for the wire.</summary>
public sealed record MessagePropertiesView(
    string? Id,
    string? Type,
    string? Source,
    string? Subject,
    string? DataSchema,
    string? ContentType,
    DateTimeOffset? Time,
    DateTimeOffset? ScheduledAt,
    string? TraceParent
);

/// <summary>One outbox row in a list.</summary>
public sealed record OutboxListItem(
    Guid Id,
    string MessageType,
    string TransportName,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ProcessedAt,
    DateTimeOffset? FailedAt,
    DateTimeOffset? ScheduledAt,
    bool IsPoisoned,
    int ErrorCount,
    int RequeuedCount,
    string? LastError
);

/// <summary>One outbox row with its payload.</summary>
public sealed record OutboxDetail(
    Guid Id,
    string MessageType,
    string TransportName,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ProcessedAt,
    DateTimeOffset? FailedAt,
    DateTimeOffset? ScheduledAt,
    bool IsPoisoned,
    int ErrorCount,
    int RequeuedCount,
    string? LastError,
    MessagePropertiesView Properties,
    string? JsonPayload,
    string PayloadBase64
);

/// <summary>One inbox handler row in a list.</summary>
public sealed record InboxListItem(
    Guid HandlerStatusId,
    string MessageId,
    string MessageType,
    string HandlerKey,
    string TransportName,
    DateTimeOffset ReceivedAt,
    DateTimeOffset? CompletedAt,
    bool IsPoisoned,
    int ErrorCount,
    int RequeuedCount,
    string? LastError
);

/// <summary>A sibling handler of the same inbox message.</summary>
public sealed record InboxSiblingHandler(
    Guid HandlerStatusId,
    string HandlerKey,
    bool IsPoisoned,
    DateTimeOffset? CompletedAt,
    int ErrorCount,
    string? LastError
);

/// <summary>One inbox handler row with its message payload and sibling handlers.</summary>
public sealed record InboxDetail(
    Guid HandlerStatusId,
    string MessageId,
    string MessageType,
    string HandlerKey,
    string TransportName,
    DateTimeOffset ReceivedAt,
    DateTimeOffset? CompletedAt,
    bool IsPoisoned,
    int ErrorCount,
    int RequeuedCount,
    string? LastError,
    MessagePropertiesView Properties,
    string? JsonPayload,
    string PayloadBase64,
    IReadOnlyList<InboxSiblingHandler> OtherHandlers
);

/// <summary>One id that could not be mutated, and why.</summary>
public sealed record MutationFailure(Guid Id, string Code, string Detail);

/// <summary>
/// The outcome of an explicit-id mutation. Per-id rather than a single count, because "requeue
/// these 40" partially failing is normal and the operator needs to know which 3 to retry.
/// </summary>
public sealed record MutationResponse(
    IReadOnlyList<Guid> Succeeded,
    IReadOnlyList<MutationFailure> Failed
)
{
    /// <summary>How many rows were mutated. Derived from <see cref="Succeeded"/>, not sent.</summary>
    [JsonIgnore]
    public int Processed => Succeeded.Count;
}

/// <summary>
/// The outcome of a filter-driven mutation. <see cref="Remaining"/> and <see cref="Capped"/> tell
/// an operator whether to run the same filter again rather than leaving them guessing.
/// </summary>
public sealed record MatchingMutationResponse(long Processed, long Remaining, bool Capped);
