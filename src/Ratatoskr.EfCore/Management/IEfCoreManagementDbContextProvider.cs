namespace Ratatoskr.EfCore.Management;

/// <summary>
/// Abstraction over one registered DbContext for management API queries.
/// One implementation is registered per <c>AddEfCoreDurability&lt;TDbContext&gt;</c> call.
/// The <see cref="EfCoreManagementProviderLookup"/> resolves the correct provider by context name.
/// </summary>
internal interface IEfCoreManagementDbContextProvider
{
    string DbContextName { get; }
    bool HasOutbox { get; }
    bool HasInbox { get; }

    // ── Outbox ──────────────────────────────────────────────────────────────

    Task<(List<OutboxPoisonedListItem> Items, long TotalCount)> ListPoisonedOutboxAsync(
        int pageSize, string? cursor, DateTimeOffset? from, DateTimeOffset? to, string? type, CancellationToken ct);

    Task<OutboxPoisonedDetail?> GetPoisonedOutboxDetailAsync(Guid id, CancellationToken ct);

    Task<SingleRequeueOutcome> RequeueOutboxAsync(Guid id, CancellationToken ct);

    Task<SingleDeleteOutcome> DeleteOutboxAsync(Guid id, CancellationToken ct);

    Task<BulkRequeueOutboxResponse> BulkRequeueOutboxAsync(List<Guid>? ids, bool all, CancellationToken ct);

    Task BulkDeleteOutboxAsync(List<Guid>? ids, bool all, CancellationToken ct);

    // ── Inbox ────────────────────────────────────────────────────────────────

    Task<(List<InboxPoisonedListItem> Items, long TotalCount)> ListPoisonedInboxAsync(
        int pageSize, string? cursor, DateTimeOffset? from, DateTimeOffset? to, string? type, CancellationToken ct);

    Task<InboxHandlerDetail?> GetPoisonedInboxDetailAsync(Guid handlerStatusId, CancellationToken ct);

    Task<InboxMessageHandlers?> GetInboxHandlersForMessageAsync(string messageId, CancellationToken ct);

    Task<SingleRequeueOutcome> RequeueInboxHandlerAsync(Guid handlerStatusId, CancellationToken ct);

    Task<RequeueMessageOutcome> RequeueAllInboxHandlersForMessageAsync(string messageId, CancellationToken ct);

    Task<SingleDeleteOutcome> DeleteInboxHandlerStatusAsync(Guid handlerStatusId, CancellationToken ct);

    Task<BulkRequeueInboxResponse> BulkRequeueInboxAsync(List<Guid>? ids, bool all, CancellationToken ct);

    Task BulkDeleteInboxAsync(List<Guid>? ids, bool all, CancellationToken ct);

    // ── Health ───────────────────────────────────────────────────────────────

    Task<ContextHealthResponse> GetHealthAsync(CancellationToken ct);
}
