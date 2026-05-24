using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Management;

namespace Ratatoskr.EfCore.Management.Endpoints.Outbox;

internal static partial class ListPoisonedOutboxEndpoint
{
    internal static void Map(IEndpointRouteBuilder outboxGroup)
    {
        outboxGroup.MapGet("/poisoned", Handle);
    }

    private static async Task<Results<Ok<OutboxPoisonedListResponse>, ProblemHttpResult>> Handle(
        string contextName,
        EfCoreManagementDbContextLookup lookup,
        ILoggerFactory loggerFactory,
        int pageSize = PaginationOptions.DefaultPageSize,
        string? cursor = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        string? search = null,
        CancellationToken ct = default
    )
    {
        var logger = loggerFactory.CreateLogger(typeof(ListPoisonedOutboxEndpoint).FullName!);
        if (
            ManagementDbContextResolver.EnsureOutbox(lookup, contextName, out var db) is
            { } resolveError
        )
        {
            return resolveError;
        }

        pageSize = PaginationOptions.ClampPageSize(pageSize);

        CursorHelper.Cursor? decodedCursor = null;
        if (cursor is not null)
        {
            if (!CursorHelper.TryDecode(cursor, out var c))
            {
                // Info, not warning: likely a malformed/stale cursor copied from an older
                // client. Surface it once so an operator can correlate 400s to their UI.
                LogRejectingMalformedCursor(logger, contextName);
                return ManagementResults.BadRequest("Invalid pagination cursor.");
            }
            decodedCursor = c;
        }

        var filtered = db.Set<OutboxMessageEntity>().AsNoTracking().Where(x => x.IsPoisoned);
        if (from.HasValue)
        {
            filtered = filtered.Where(x => x.CreatedAt >= from.Value);
        }

        if (to.HasValue)
        {
            filtered = filtered.Where(x => x.CreatedAt <= to.Value);
        }

        if (search is not null)
        {
            var pattern = ManagementHelpers.BuildSearchPattern(search);
            filtered = filtered.Where(x =>
                EF.Functions.Like(x.SerializedProperties, pattern, @"\")
            );
        }

        var paged = filtered;
        if (decodedCursor is { } k)
        {
            // Tuple comparison: (CreatedAt, Id) > (cursor.Time, cursor.Id).
            // Expressed as OR-form so EF translates cleanly on both Postgres and SQL Server.
            paged = paged.Where(x =>
                x.CreatedAt > k.Time || (x.CreatedAt == k.Time && x.Id > k.Id)
            );
        }

        // Deliberately fetch pageSize + 1 so we can determine whether another page exists
        // without a separate round-trip.
        var items = await paged
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .Take(pageSize + 1)
            .Select(x => new
            {
                x.Id,
                x.SerializedProperties,
                x.CreatedAt,
                x.ErrorCount,
                x.RequeuedCount,
                x.Error,
            })
            .ToListAsync(ct);

        var hasNext = items.Count > pageSize;
        if (hasNext)
        {
            items.RemoveAt(items.Count - 1);
        }

        var dtos = items
            .Select(x => new OutboxPoisonedListItem(
                x.Id,
                ManagementHelpers.ExtractType(x.SerializedProperties, logger),
                x.CreatedAt,
                x.ErrorCount,
                x.RequeuedCount,
                string.IsNullOrEmpty(x.Error) ? null : x.Error,
                contextName
            ))
            .ToList();

        var nextCursor = hasNext ? CursorHelper.Encode(items[^1].CreatedAt, items[^1].Id) : null;

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
        string DbContext
    );

    internal record OutboxPoisonedListResponse(
        List<OutboxPoisonedListItem> Items,
        long TotalCount,
        string? NextCursor
    );

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Rejecting management list request with malformed cursor (context {ContextName})."
    )]
    private static partial void LogRejectingMalformedCursor(ILogger logger, string contextName);
}
