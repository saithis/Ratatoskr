using System.Text.Json.Serialization;

namespace Ratatoskr.Management.Contracts;

/// <summary>The lifecycle states a message row can be filtered by.</summary>
public enum MessageStatusFilter
{
    /// <summary>Every row, regardless of state. Refused in combination with an unbounded search.</summary>
    All,

    /// <summary>Rows that exhausted their retries and stopped being processed.</summary>
    Poisoned,

    /// <summary>Rows still waiting to be processed.</summary>
    Pending,

    /// <summary>Rows that finished — sent, for the outbox; handled, for the inbox.</summary>
    Completed,
}

/// <summary>
/// Selects a set of message rows. The same filter shape drives list, count and both matching
/// mutations, so what an operator previews is exactly what they then act on.
/// </summary>
public sealed record MessageFilter
{
    /// <summary>Which lifecycle state to include. Defaults to poisoned, the common case.</summary>
    public MessageStatusFilter Status { get; init; } = MessageStatusFilter.Poisoned;

    /// <summary>Only rows created at or after this instant.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>Only rows created at or before this instant.</summary>
    public DateTimeOffset? To { get; init; }

    /// <summary>
    /// A substring match over the row's serialized CloudEvents properties. This is a LIKE with a
    /// leading wildcard, not a full-text index, so it cannot use an index and must be paired with
    /// a bounded window or a narrower status — see <see cref="IsUnboundedSearch"/>.
    /// </summary>
    public string? Search { get; init; }

    /// <summary>
    /// Whether this filter selects everything. A destructive matching operation refuses an empty
    /// filter, so "apply to absolutely everything" is never reachable by omission.
    /// </summary>
    [JsonIgnore]
    public bool IsEmpty =>
        Status is MessageStatusFilter.All
        && From is null
        && To is null
        && string.IsNullOrWhiteSpace(Search);

    /// <summary>
    /// Whether this filter asks for a full-table substring scan: a search with neither a bounded
    /// time window nor a status narrower than <see cref="MessageStatusFilter.All"/>.
    /// </summary>
    [JsonIgnore]
    public bool IsUnboundedSearch =>
        !string.IsNullOrWhiteSpace(Search)
        && Status is MessageStatusFilter.All
        && From is null
        && To is null;
}

/// <summary>Requests one keyset page of message rows.</summary>
public sealed record ListMessagesRequest
{
    /// <summary>Which rows to include.</summary>
    public MessageFilter Filter { get; init; } = new();

    /// <summary>The opaque cursor from the previous page, or null to start at the beginning.</summary>
    public string? Cursor { get; init; }

    /// <summary>Page size, clamped to the server's bounds.</summary>
    public int Limit { get; init; } = ManagementPaging.DefaultPageSize;
}

/// <summary>Asks how many rows a filter matches.</summary>
public sealed record CountMessagesRequest
{
    /// <summary>Which rows to count.</summary>
    public MessageFilter Filter { get; init; } = new();
}

/// <summary>Addresses one outbox row or one inbox handler row by id.</summary>
public sealed record GetMessageRequest(Guid Id);

/// <summary>Addresses every poisoned handler of one inbox message.</summary>
public sealed record RequeueInboxMessageRequest(string MessageId);

/// <summary>Mutates an explicit list of ids.</summary>
public sealed record MutateByIdsRequest
{
    /// <summary>
    /// The rows to act on. Capped at <see cref="ManagementPaging.MaxMutationIds"/>, which fits
    /// comfortably inside typical SQL parameter limits.
    /// </summary>
    public IReadOnlyList<Guid> Ids { get; init; } = [];
}

/// <summary>Mutates every row a filter matches, in bounded server-side batches.</summary>
public sealed record MutateMatchingRequest
{
    /// <summary>
    /// Which rows to act on. Must not be empty: an unfiltered destructive operation is refused
    /// with <see cref="ManagementErrorCodes.FilterRequired"/>.
    /// </summary>
    public MessageFilter Filter { get; init; } = new();

    /// <summary>
    /// The upper bound on rows this run will touch. Defaults to the server's cap when omitted,
    /// and is clamped to it when larger.
    /// </summary>
    public int? MaxTotalOperations { get; init; }
}

/// <summary>Asks one service to describe itself.</summary>
public sealed record DescribeServiceRequest;

/// <summary>Asks one service which DbContexts it exposes.</summary>
public sealed record ListContextsRequest;

/// <summary>Asks for one DbContext's health gauges.</summary>
public sealed record ContextHealthRequest;

/// <summary>Server-side paging and mutation bounds, shared by every caller.</summary>
public static class ManagementPaging
{
    /// <summary>The page size used when a caller does not ask for one.</summary>
    public const int DefaultPageSize = 50;

    /// <summary>The smallest page a caller may ask for.</summary>
    public const int MinPageSize = 1;

    /// <summary>The largest page a caller may ask for.</summary>
    public const int MaxPageSize = 200;

    /// <summary>The most ids one explicit-list mutation may name.</summary>
    public const int MaxMutationIds = 1000;

    /// <summary>Rows committed per transaction by a matching mutation.</summary>
    public const int MatchingBatchSize = 500;

    /// <summary>The default ceiling on rows one matching mutation will touch.</summary>
    public const int DefaultMaxTotalOperations = 10_000;

    /// <summary>Clamps a requested page size into range.</summary>
    public static int ClampPageSize(int pageSize) =>
        Math.Clamp(pageSize, MinPageSize, MaxPageSize);
}
