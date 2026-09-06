namespace Ratatoskr.Management.Contracts;

public sealed record ServiceHeartbeat
{
    public required string ServiceName { get; init; }
    public required string InstanceId { get; init; }
    public required string MachineName { get; init; }
    public string? Environment { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public List<DbContextSummaryDto> DbContexts { get; init; } = [];
    public List<ChannelSummaryDto> Channels { get; init; } = [];
}

public sealed record DbContextSummaryDto
{
    public required string DbContextName { get; init; }
    public bool HasOutbox { get; init; }
    public bool HasInbox { get; init; }
    public long PendingOutboxCount { get; init; }
    public long PoisonedOutboxCount { get; init; }
    public long PendingInboxCount { get; init; }
    public long PoisonedInboxCount { get; init; }
}

public sealed record ChannelSummaryDto
{
    public required string ChannelName { get; init; }
    public required string ChannelType { get; init; }
    public required string TransportName { get; init; }
    public List<string> MessageTypes { get; init; } = [];
    public string? QueueName { get; init; }
    public string? ExchangeName { get; init; }
}

public sealed record ManagementRequestEnvelope
{
    public required string RequestId { get; init; }
    public required string Action { get; init; }
    public required string TargetService { get; init; }
    public string? TargetContext { get; init; }
    public string? PayloadJson { get; init; }
}

public sealed record ManagementResponseEnvelope
{
    public required string RequestId { get; init; }
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public string? PayloadJson { get; init; }

    public static ManagementResponseEnvelope Ok(string requestId, string? payloadJson = null) =>
        new() { RequestId = requestId, Success = true, PayloadJson = payloadJson };

    public static ManagementResponseEnvelope Error(string requestId, string errorMessage) =>
        new() { RequestId = requestId, Success = false, ErrorMessage = errorMessage };
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize);

public sealed record OutboxItemDto(
    Guid Id,
    string TransportName,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ProcessedAt,
    DateTimeOffset? FailedAt,
    bool IsPoisoned,
    short ErrorCount,
    string Error,
    int RequeuedCount,
    DateTimeOffset? ScheduledAt
);

public sealed record MessagePropertiesDto(
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

public sealed record OutboxDetailDto(
    Guid Id,
    string TransportName,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ProcessedAt,
    DateTimeOffset? FailedAt,
    bool IsPoisoned,
    short ErrorCount,
    string Error,
    int RequeuedCount,
    DateTimeOffset? ScheduledAt,
    MessagePropertiesDto? Properties,
    string? Content
);

public sealed record InboxItemDto(
    Guid Id,
    string MessageId,
    string HandlerKey,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    bool IsPoisoned,
    short ErrorCount,
    string LastError,
    int RequeuedCount,
    string TransportName
);

public sealed record InboxOtherHandlerDto(
    Guid Id,
    string HandlerKey,
    bool IsPoisoned,
    DateTimeOffset? CompletedAt,
    short ErrorCount,
    string LastError
);

public sealed record InboxDetailDto(
    Guid Id,
    string MessageId,
    string HandlerKey,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    bool IsPoisoned,
    short ErrorCount,
    string LastError,
    int RequeuedCount,
    string TransportName,
    MessagePropertiesDto? Properties,
    string? Content,
    IReadOnlyList<InboxOtherHandlerDto> OtherHandlers
);

public sealed record RequeueResultDto(int RequeuedCount);
public sealed record DeleteResultDto(int DeletedCount);
