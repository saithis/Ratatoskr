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

    private static async Task<Results<Ok<InboxPoisonedListResponse>, NotFound>> Handle(
        string contextName,
        EfCoreManagementProviderLookup lookup,
        IServiceScopeFactory scopeFactory,
        int pageSize = 50,
        string? cursor = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        string? type = null,
        CancellationToken ct = default)
    {
        var provider = lookup.Find(contextName);
        if (provider is null || !provider.HasInbox) return TypedResults.NotFound();

        using var scope = scopeFactory.CreateScope();
        var db = provider.GetDbContext(scope.ServiceProvider);
        db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

        var query =
            from hs in db.Set<InboxHandlerStatusEntity>()
            join msg in db.Set<InboxMessageEntity>() on hs.MessageId equals msg.Id
            where hs.IsPoisoned
            select new { hs, msg };

        if (from.HasValue) query = query.Where(x => x.msg.ReceivedAt >= from.Value);
        if (to.HasValue) query = query.Where(x => x.msg.ReceivedAt <= to.Value);
        if (type is not null) query = query.Where(x => EF.Functions.Like(x.msg.SerializedProperties, $"%{type}%"));

        if (cursor is not null)
        {
            var lastId = CursorHelper.DecodeCursor(cursor);
            if (lastId.HasValue)
                query = query.Where(x => x.hs.Id.CompareTo(lastId.Value) > 0);
        }

        var totalCount = await query.LongCountAsync(ct);

        var items = await query
            .OrderBy(x => x.msg.ReceivedAt).ThenBy(x => x.hs.Id)
            .Take(pageSize)
            .Select(x => new
            {
                x.hs.Id, x.hs.MessageId, x.hs.HandlerKey,
                x.hs.ErrorCount, x.hs.RequeuedCount, x.hs.LastError,
                x.msg.ReceivedAt, x.msg.SerializedProperties
            })
            .ToListAsync(ct);

        var dtos = items
            .Select(x => new InboxPoisonedListItem(
                x.Id, x.MessageId,
                ManagementHelpers.ExtractType(x.SerializedProperties),
                x.HandlerKey, x.ReceivedAt, x.ErrorCount, x.RequeuedCount,
                string.IsNullOrEmpty(x.LastError) ? null : x.LastError,
                provider.DbContextName))
            .ToList();

        var nextCursor = dtos.Count == pageSize ? CursorHelper.EncodeCursor(dtos[^1].HandlerStatusId) : null;
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
