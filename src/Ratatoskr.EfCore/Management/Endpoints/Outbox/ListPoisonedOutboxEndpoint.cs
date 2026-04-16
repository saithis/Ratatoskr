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

    private static async Task<Results<Ok<OutboxPoisonedListResponse>, NotFound>> Handle(
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
        if (provider is null || !provider.HasOutbox) return TypedResults.NotFound();

        using var scope = scopeFactory.CreateScope();
        var db = provider.GetDbContext(scope.ServiceProvider);
        db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

        var query = db.Set<OutboxMessageEntity>().Where(x => x.IsPoisoned);
        if (from.HasValue) query = query.Where(x => x.CreatedAt >= from.Value);
        if (to.HasValue) query = query.Where(x => x.CreatedAt <= to.Value);
        if (type is not null) query = query.Where(x => EF.Functions.Like(x.SerializedProperties, $"%{type}%"));

        if (cursor is not null)
        {
            var lastId = CursorHelper.DecodeCursor(cursor);
            if (lastId.HasValue)
                query = query.Where(x => x.Id.CompareTo(lastId.Value) > 0);
        }

        var totalCount = await query.LongCountAsync(ct);

        var items = await query
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
            .Take(pageSize)
            .Select(x => new { x.Id, x.SerializedProperties, x.CreatedAt, x.ErrorCount, x.RequeuedCount, x.Error })
            .ToListAsync(ct);

        var dtos = items
            .Select(x => new OutboxPoisonedListItem(
                x.Id,
                ManagementHelpers.ExtractType(x.SerializedProperties),
                x.CreatedAt, x.ErrorCount, x.RequeuedCount,
                string.IsNullOrEmpty(x.Error) ? null : x.Error,
                provider.DbContextName))
            .ToList();

        var nextCursor = dtos.Count == pageSize ? CursorHelper.EncodeCursor(dtos[^1].Id) : null;
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
