using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Management;

namespace Ratatoskr.EfCore.Management.Endpoints.Inbox;

internal static partial class ListPoisonedInboxEndpoint
{
    internal static void Map(IEndpointRouteBuilder inboxGroup)
    {
        inboxGroup.MapGet("/poisoned", HandleAsync);
    }

    private static async Task<
        Results<Ok<InboxPoisonedListResponse>, ProblemHttpResult>
    > HandleAsync(
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
        var logger = loggerFactory.CreateLogger(typeof(ListPoisonedInboxEndpoint).FullName!);
        if (
            ManagementDbContextResolver.EnsureInbox(lookup, contextName, out var db) is
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
                LogRejectingMalformedCursor(logger, contextName);
                return ManagementResults.BadRequest("Invalid pagination cursor.");
            }
            decodedCursor = c;
        }

        var filtered =
            from hs in db.Set<InboxHandlerStatusEntity>().AsNoTracking()
            join msg in db.Set<InboxMessageEntity>() on hs.MessageId equals msg.Id
            where hs.IsPoisoned
            select new { hs, msg };

        if (from.HasValue)
        {
            filtered = filtered.Where(x => x.msg.ReceivedAt >= from.Value);
        }

        if (to.HasValue)
        {
            filtered = filtered.Where(x => x.msg.ReceivedAt <= to.Value);
        }

        if (search is not null)
        {
            var pattern = ManagementHelpers.BuildSearchPattern(search);
            filtered = filtered.Where(x =>
                EF.Functions.Like(x.msg.SerializedProperties, pattern, @"\")
            );
        }

        var paged = filtered;
        if (decodedCursor is { } k)
        {
            // Tuple comparison keyed on (ReceivedAt, HandlerStatusId) which matches the ORDER BY.
            paged = paged.Where(x =>
                x.msg.ReceivedAt > k.Time || (x.msg.ReceivedAt == k.Time && x.hs.Id > k.Id)
            );
        }

        var rows = await paged
            .OrderBy(x => x.msg.ReceivedAt)
            .ThenBy(x => x.hs.Id)
            .Take(pageSize + 1)
            .Select(x => new
            {
                x.hs.Id,
                x.hs.MessageId,
                x.hs.HandlerKey,
                x.hs.ErrorCount,
                x.hs.RequeuedCount,
                x.hs.LastError,
                x.msg.ReceivedAt,
                x.msg.SerializedProperties,
            })
            .ToListAsync(ct);

        var hasNext = rows.Count > pageSize;
        if (hasNext)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var dtos = rows.ConvertAll(x => new InboxPoisonedListItem(
            x.Id,
            x.MessageId,
            ManagementHelpers.ExtractType(x.SerializedProperties, logger),
            x.HandlerKey,
            x.ReceivedAt,
            x.ErrorCount,
            x.RequeuedCount,
            string.IsNullOrEmpty(x.LastError) ? null : x.LastError,
            contextName
        ));

        var nextCursor = hasNext ? CursorHelper.Encode(rows[^1].ReceivedAt, rows[^1].Id) : null;

        var totalCount = await filtered.LongCountAsync(ct);
        return TypedResults.Ok(new InboxPoisonedListResponse(dtos, totalCount, nextCursor));
    }

    [SuppressMessage("ReSharper", "NotAccessedPositionalProperty.Global", Justification = "DTO")]
    internal record InboxPoisonedListItem(
        Guid HandlerStatusId,
        string MessageId,
        string MessageType,
        string HandlerKey,
        DateTimeOffset ReceivedAt,
        int ErrorCount,
        int RequeuedCount,
        string? LastError,
        string DbContext
    );

    [SuppressMessage("ReSharper", "NotAccessedPositionalProperty.Global", Justification = "DTO")]
    internal record InboxPoisonedListResponse(
        List<InboxPoisonedListItem> Items,
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
