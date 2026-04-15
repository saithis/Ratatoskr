using Microsoft.AspNetCore.Http;
using Ratatoskr.EfCore.Management.Dto;

namespace Ratatoskr.EfCore.Management;

/// <summary>
/// Abstraction over one registered DbContext for management API queries.
/// One implementation is registered per <c>AddEfCoreDurability&lt;TDbContext&gt;</c> call.
/// The non-generic <see cref="EfCoreEndpointConfigurator"/> aggregates across all providers.
/// </summary>
internal interface IEfCoreManagementDbContextProvider
{
    string DbContextName { get; }
    bool HasOutbox { get; }
    bool HasInbox { get; }

    Task<(List<OutboxPoisonedListItemDto> Items, long TotalCount)> GetPoisonedOutboxAsync(
        int pageSize, string? cursor, DateTimeOffset? from, DateTimeOffset? to, string? type, CancellationToken ct);

    Task<OutboxPoisonedDetailDto?> GetPoisonedOutboxDetailAsync(Guid id, CancellationToken ct);
    Task<IResult> RequeueOutboxAsync(Guid id, CancellationToken ct);
    Task<IResult> DeleteOutboxAsync(Guid id, CancellationToken ct);
    Task<BulkActionResult> BulkRequeueOutboxAsync(List<Guid>? ids, bool all, CancellationToken ct);
    Task<IResult> BulkDeleteOutboxAsync(List<Guid>? ids, bool all, CancellationToken ct);

    Task<(List<InboxPoisonedListItemDto> Items, long TotalCount)> GetPoisonedInboxAsync(
        int pageSize, string? cursor, DateTimeOffset? from, DateTimeOffset? to, string? type, CancellationToken ct);

    Task<InboxPoisonedDetailDto?> GetPoisonedInboxDetailAsync(Guid handlerStatusId, CancellationToken ct);
    Task<InboxMessageHandlersDto?> GetInboxHandlersForMessageAsync(string messageId, CancellationToken ct);
    Task<IResult> RequeueInboxHandlerAsync(Guid handlerStatusId, CancellationToken ct);
    Task<IResult> RequeueAllInboxHandlersForMessageAsync(string messageId, CancellationToken ct);
    Task<IResult> DeleteInboxHandlerStatusAsync(Guid handlerStatusId, CancellationToken ct);
    Task<BulkActionResult> BulkRequeueInboxAsync(List<Guid>? ids, bool all, CancellationToken ct);
    Task<IResult> BulkDeleteInboxAsync(List<Guid>? ids, bool all, CancellationToken ct);

    Task<DbContextHealthDto> GetHealthAsync(CancellationToken ct);
}
