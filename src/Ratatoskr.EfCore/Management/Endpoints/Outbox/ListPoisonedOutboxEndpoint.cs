using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Ratatoskr.EfCore.Management;

internal record OutboxPoisonedListItem(
    Guid Id,
    string MessageType,
    DateTimeOffset CreatedAt,
    int ErrorCount,
    int RequeuedCount,
    string? LastError,
    string DbContext);

internal record OutboxPoisonedListResponse(
    List<OutboxPoisonedListItem> Items,
    long TotalCount,
    string? NextCursor);

internal static class ListPoisonedOutboxEndpoint
{
    internal static void Map(RouteGroupBuilder outboxGroup)
    {
        outboxGroup.MapGet("/poisoned", Handle);
    }

    private static async Task<Results<Ok<OutboxPoisonedListResponse>, NotFound>> Handle(
        string contextName,
        EfCoreManagementProviderLookup lookup,
        int pageSize = 50,
        string? cursor = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        string? type = null,
        CancellationToken ct = default)
    {
        var provider = lookup.Find(contextName);
        if (provider is null || !provider.HasOutbox) return TypedResults.NotFound();

        var (items, totalCount) = await provider.ListPoisonedOutboxAsync(pageSize, cursor, from, to, type, ct);
        var nextCursor = items.Count == pageSize ? CursorHelper.EncodeCursor(items[^1].Id) : null;
        return TypedResults.Ok(new OutboxPoisonedListResponse(items, totalCount, nextCursor));
    }
}
