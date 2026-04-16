using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.EfCore.Internal;

namespace Ratatoskr.EfCore.Management;

internal static class ListPoisonedInboxEndpoint
{
    internal static void Map(RouteGroupBuilder inboxGroup)
    {
        inboxGroup.MapGet("/poisoned", Handle);
    }

    private static async Task<Results<Ok<InboxPoisonedListResponse>, NotFound, BadRequest<string>>> Handle(
        string contextName,
        EfCoreManagementProviderLookup lookup,
        IServiceScopeFactory scopeFactory,
        int pageSize = PaginationOptions.DefaultPageSize,
        string? cursor = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        string? type = null,
        CancellationToken ct = default)
    {
        var provider = lookup.Find(contextName);
        if (provider is null || !provider.HasInbox) return TypedResults.NotFound();

        pageSize = PaginationOptions.ClampPageSize(pageSize);

        CursorHelper.Cursor? decodedCursor = null;
        if (cursor is not null)
        {
            if (!CursorHelper.TryDecode(cursor, out var c))
                return TypedResults.BadRequest("Invalid cursor.");
            decodedCursor = c;
        }

        using var scope = scopeFactory.CreateScope();
        var db = provider.GetDbContext(scope.ServiceProvider);
        db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

        var filtered =
            from hs in db.Set<InboxHandlerStatusEntity>()
            join msg in db.Set<InboxMessageEntity>() on hs.MessageId equals msg.Id
            where hs.IsPoisoned
            select new { hs, msg };

        if (from.HasValue) filtered = filtered.Where(x => x.msg.ReceivedAt >= from.Value);
        if (to.HasValue) filtered = filtered.Where(x => x.msg.ReceivedAt <= to.Value);
        if (type is not null) filtered = filtered.Where(x => EF.Functions.Like(x.msg.SerializedProperties, $"%{type}%"));

        var paged = filtered;
        if (decodedCursor is { } k)
        {
            // Tuple comparison keyed on (ReceivedAt, HandlerStatusId) which matches the ORDER BY.
            paged = paged.Where(x => x.msg.ReceivedAt > k.Time
                                     || (x.msg.ReceivedAt == k.Time && x.hs.Id.CompareTo(k.Id) > 0));
        }

        var rows = await paged
            .OrderBy(x => x.msg.ReceivedAt).ThenBy(x => x.hs.Id)
            .Take(pageSize + 1)
            .Select(x => new
            {
                x.hs.Id, x.hs.MessageId, x.hs.HandlerKey,
                x.hs.ErrorCount, x.hs.RequeuedCount, x.hs.LastError,
                x.msg.ReceivedAt, x.msg.SerializedProperties
            })
            .ToListAsync(ct);

        var hasNext = rows.Count > pageSize;
        if (hasNext) rows.RemoveAt(rows.Count - 1);

        var dtos = rows
            .Select(x => new InboxPoisonedListItem(
                x.Id, x.MessageId,
                ManagementHelpers.ExtractType(x.SerializedProperties),
                x.HandlerKey, x.ReceivedAt, x.ErrorCount, x.RequeuedCount,
                string.IsNullOrEmpty(x.LastError) ? null : x.LastError,
                provider.DbContextName))
            .ToList();

        var nextCursor = hasNext
            ? CursorHelper.Encode(rows[^1].ReceivedAt, rows[^1].Id)
            : null;

        var totalCount = await filtered.LongCountAsync(ct);
        return TypedResults.Ok(new InboxPoisonedListResponse(dtos, totalCount, nextCursor));
    }

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
}
