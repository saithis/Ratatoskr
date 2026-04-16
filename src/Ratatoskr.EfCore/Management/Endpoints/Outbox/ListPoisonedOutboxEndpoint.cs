using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.EfCore.Internal;

namespace Ratatoskr.EfCore.Management;

internal static class ListPoisonedOutboxEndpoint
{
    internal static void Map(RouteGroupBuilder outboxGroup)
    {
        outboxGroup.MapGet("/poisoned", Handle);
    }

    private static async Task<Results<Ok<OutboxPoisonedListResponse>, ProblemHttpResult>> Handle(
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
        if (provider is null || !provider.HasOutbox)
            return ManagementResults.NotFound($"No outbox is registered for DbContext '{contextName}'.");

        pageSize = PaginationOptions.ClampPageSize(pageSize);

        CursorHelper.Cursor? decodedCursor = null;
        if (cursor is not null)
        {
            if (!CursorHelper.TryDecode(cursor, out var c))
                return ManagementResults.BadRequest("Invalid pagination cursor.");
            decodedCursor = c;
        }

        using var scope = scopeFactory.CreateScope();
        var db = provider.GetDbContext(scope.ServiceProvider);
        db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

        var filtered = db.Set<OutboxMessageEntity>().Where(x => x.IsPoisoned);
        if (from.HasValue) filtered = filtered.Where(x => x.CreatedAt >= from.Value);
        if (to.HasValue) filtered = filtered.Where(x => x.CreatedAt <= to.Value);
        if (type is not null)
        {
            var pattern = ManagementHelpers.BuildMessageTypeLikePattern(type);
            filtered = filtered.Where(x => EF.Functions.Like(x.SerializedProperties, pattern));
        }

        var paged = filtered;
        if (decodedCursor is { } k)
        {
            // Tuple comparison: (CreatedAt, Id) > (cursor.Time, cursor.Id).
            // Expressed as OR-form so EF translates cleanly on both Postgres and SQL Server.
            paged = paged.Where(x => x.CreatedAt > k.Time || (x.CreatedAt == k.Time && x.Id.CompareTo(k.Id) > 0));
        }

        // Deliberately fetch pageSize + 1 so we can determine whether another page exists
        // without a separate round-trip.
        var items = await paged
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
            .Take(pageSize + 1)
            .Select(x => new { x.Id, x.SerializedProperties, x.CreatedAt, x.ErrorCount, x.RequeuedCount, x.Error })
            .ToListAsync(ct);

        var hasNext = items.Count > pageSize;
        if (hasNext) items.RemoveAt(items.Count - 1);

        var dtos = items
            .Select(x => new OutboxPoisonedListItem(
                x.Id,
                ManagementHelpers.ExtractType(x.SerializedProperties),
                x.CreatedAt, x.ErrorCount, x.RequeuedCount,
                string.IsNullOrEmpty(x.Error) ? null : x.Error,
                provider.DbContextName))
            .ToList();

        // Belt-and-braces exact match — the DB-side LIKE is a coarse prefilter and
        // may over-match on providers that do not honour the '\' LIKE escape char.
        if (type is not null)
            dtos = dtos.Where(x => x.MessageType == type).ToList();

        var nextCursor = hasNext
            ? CursorHelper.Encode(items[^1].CreatedAt, items[^1].Id)
            : null;

        // Total reflects the full filtered set, not the remainder-after-cursor, so the UI can
        // display progress consistently across pages.
        var totalCount = await filtered.LongCountAsync(ct);
        return TypedResults.Ok(new OutboxPoisonedListResponse(dtos, totalCount, nextCursor));
    }

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
}
