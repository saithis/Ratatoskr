using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Ratatoskr.EfCore.Management;

internal record InboxPoisonedListItem(
    Guid HandlerStatusId,
    string MessageId,
    string MessageType,
    string HandlerKey,
    DateTimeOffset ReceivedAt,
    int ErrorCount,
    int RequeuedCount,
    string? LastError,
    string DbContext);

internal record InboxPoisonedListResponse(
    List<InboxPoisonedListItem> Items,
    long TotalCount,
    string? NextCursor);

internal static class ListPoisonedInboxEndpoint
{
    internal static void Map(RouteGroupBuilder inboxGroup)
    {
        inboxGroup.MapGet("/poisoned", Handle);
    }

    private static async Task<Results<Ok<InboxPoisonedListResponse>, NotFound>> Handle(
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
        if (provider is null || !provider.HasInbox) return TypedResults.NotFound();

        var (items, totalCount) = await provider.ListPoisonedInboxAsync(pageSize, cursor, from, to, type, ct);
        var nextCursor = items.Count == pageSize ? CursorHelper.EncodeCursor(items[^1].HandlerStatusId) : null;
        return TypedResults.Ok(new InboxPoisonedListResponse(items, totalCount, nextCursor));
    }
}
