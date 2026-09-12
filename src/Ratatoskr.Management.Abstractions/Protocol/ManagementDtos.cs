namespace Ratatoskr.Management.Contracts;

public sealed record ServiceHeartbeat
{
    public ProtocolVersion ProtocolVersion { get; init; } = ManagementProtocol.Current;
    public required string ServiceName { get; init; }
    public required string InstanceId { get; init; }
    public required string MachineName { get; init; }
    public string? Environment { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    /// <summary>
    /// Provider-specific endpoint at which this instance accepts management commands.
    /// Providers treat this as an opaque address; it is not exposed by the dashboard API.
    /// </summary>
    public string? ManagementEndpoint { get; init; }
    public IReadOnlyList<CapabilityDescriptor> Capabilities { get; init; } = [];
    public List<DbContextSummaryDto> DbContexts { get; init; } = [];
    public List<ChannelTopology> Channels { get; init; } = [];
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

/// <summary>
/// A keyset-paginated result. <see cref="NextCursor"/> is opaque to callers and is
/// supplied as <see cref="CursorPageRequest.Cursor"/> to read the following page.
/// </summary>
public sealed record CursorPagedResult<T>(IReadOnlyList<T> Items, string? NextCursor);
public sealed record OutboxItemDto(Guid Id, string TransportName, DateTimeOffset CreatedAt, DateTimeOffset? ProcessedAt, DateTimeOffset? FailedAt, bool IsPoisoned, short ErrorCount, string Error, int RequeuedCount, DateTimeOffset? ScheduledAt);
public sealed record MessagePropertiesDto(string? Id, string? Type, string? Source, string? Subject, string? DataSchema, string? ContentType, DateTimeOffset? Time, DateTimeOffset? ScheduledAt, string? TraceParent);
public sealed record OutboxDetailDto(Guid Id, string TransportName, DateTimeOffset CreatedAt, DateTimeOffset? ProcessedAt, DateTimeOffset? FailedAt, bool IsPoisoned, short ErrorCount, string Error, int RequeuedCount, DateTimeOffset? ScheduledAt, MessagePropertiesDto? Properties, string? Content);
public sealed record InboxItemDto(Guid Id, string MessageId, string HandlerKey, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt, bool IsPoisoned, short ErrorCount, string LastError, int RequeuedCount, string TransportName);
public sealed record InboxOtherHandlerDto(Guid Id, string HandlerKey, bool IsPoisoned, DateTimeOffset? CompletedAt, short ErrorCount, string LastError);
public sealed record InboxDetailDto(Guid Id, string MessageId, string HandlerKey, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt, bool IsPoisoned, short ErrorCount, string LastError, int RequeuedCount, string TransportName, MessagePropertiesDto? Properties, string? Content, IReadOnlyList<InboxOtherHandlerDto> OtherHandlers);
public sealed record RequeueResultDto(int RequeuedCount);
public sealed record DeleteResultDto(int DeletedCount);

public sealed record GetOutboxMessagesRequest(string? Status = "Poisoned", string? Cursor = null, int Limit = 20);
public sealed record GetOutboxDetailRequest(Guid Id);
public sealed record RequeueOutboxRequest(Guid Id);
public sealed record DeleteOutboxRequest(Guid Id);
public sealed record BulkRequeueOutboxRequest;
public sealed record BulkDeleteOutboxRequest;
public sealed record GetInboxMessagesRequest(string? Status = "Poisoned", string? Cursor = null, int Limit = 20);
public sealed record GetInboxDetailRequest(Guid StatusId);
public sealed record RequeueInboxHandlerRequest(Guid StatusId);
public sealed record RequeueInboxMessageRequest(string MessageId);
public sealed record DeleteInboxHandlerRequest(Guid StatusId);
public sealed record BulkRequeueInboxRequest;
public sealed record BulkDeleteInboxRequest;
