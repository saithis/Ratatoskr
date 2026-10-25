using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Ratatoskr.Management;

namespace Ratatoskr.EfCore.Management;

/// <summary>
/// Query-string parameters shared by the "list poisoned" endpoints.
/// </summary>
internal sealed record PoisonedListRequest(
    string ContextName,
    int PageSize,
    string? Cursor,
    DateTimeOffset? From,
    DateTimeOffset? To,
    string? Search
);

[SuppressMessage("ReSharper", "NotAccessedPositionalProperty.Global", Justification = "DTO")]
internal record PoisonedListResponse<TItem>(List<TItem> Items, long TotalCount, string? NextCursor);

internal static class PoisonedListQuery
{
    /// <summary>
    /// Starts a keyset-paged list over <paramref name="poisoned"/>, ordered by
    /// <c>(<paramref name="time"/>, <paramref name="id"/>)</c>. The date range filter applies to
    /// <paramref name="time"/> and the search filter to <paramref name="serializedProperties"/>.
    /// </summary>
    internal static PoisonedListQuery<TSource> For<TSource>(
        IQueryable<TSource> poisoned,
        Expression<Func<TSource, DateTimeOffset>> time,
        Expression<Func<TSource, Guid>> id,
        Expression<Func<TSource, string>> serializedProperties
    ) => new(poisoned, time, id, serializedProperties);
}

internal sealed partial class PoisonedListQuery<TSource>(
    IQueryable<TSource> poisoned,
    Expression<Func<TSource, DateTimeOffset>> time,
    Expression<Func<TSource, Guid>> id,
    Expression<Func<TSource, string>> serializedProperties
)
{
    /// <summary>
    /// Applies the filters and the cursor, fetches one page projected with
    /// <paramref name="projection"/> and maps every row with <paramref name="toItem"/>.
    /// Returns a 400 problem response for a malformed cursor.
    /// </summary>
    internal async Task<Results<Ok<PoisonedListResponse<TItem>>, ProblemHttpResult>> ExecuteAsync<
        TRow,
        TItem
    >(
        PoisonedListRequest request,
        Expression<Func<TSource, TRow>> projection,
        Func<TRow, TItem> toItem,
        ILogger logger,
        CancellationToken ct
    )
    {
        var pageSize = PaginationOptions.ClampPageSize(request.PageSize);

        CursorHelper.Cursor? after = null;
        if (request.Cursor is not null)
        {
            if (!CursorHelper.TryDecode(request.Cursor, out var decoded))
            {
                // Info, not warning: likely a malformed/stale cursor copied from an older
                // client. Surface it once so an operator can correlate 400s to their UI.
                LogRejectingMalformedCursor(logger, request.ContextName);
                return ManagementResults.BadRequest("Invalid pagination cursor.");
            }
            after = decoded;
        }

        var filtered = poisoned;
        if (request.From is { } from)
        {
            filtered = filtered.Where(Compare(time, () => from, Expression.GreaterThanOrEqual));
        }

        if (request.To is { } to)
        {
            filtered = filtered.Where(Compare(time, () => to, Expression.LessThanOrEqual));
        }

        if (request.Search is not null)
        {
            var pattern = ManagementHelpers.BuildSearchPattern(request.Search);
            filtered = filtered.Where(Like(pattern));
        }

        var paged = after is { } k ? filtered.Where(After(k)) : filtered;

        // Deliberately fetch pageSize + 1 so we can determine whether another page exists
        // without a separate round-trip.
        var rows = await paged
            .OrderBy(time)
            .ThenBy(id)
            .Take(pageSize + 1)
            .Select(KeyedProjection(projection))
            .ToListAsync(ct);

        var hasNext = rows.Count > pageSize;
        if (hasNext)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var nextCursor = hasNext ? CursorHelper.Encode(rows[^1].Time, rows[^1].Id) : null;
        var items = rows.ConvertAll(r => toItem(r.Row));

        // Total reflects the full filtered set, not the remainder-after-cursor, so the UI can
        // display progress consistently across pages.
        var totalCount = await filtered.LongCountAsync(ct);
        return TypedResults.Ok(new PoisonedListResponse<TItem>(items, totalCount, nextCursor));
    }

    /// <summary>
    /// <c>x =&gt; op(time(x), value())</c>. The value is read through a closure so EF Core
    /// sends it as a query parameter rather than inlining a constant.
    /// </summary>
    private static Expression<Func<TSource, bool>> Compare<T>(
        Expression<Func<TSource, T>> selector,
        Expression<Func<T>> value,
        Func<Expression, Expression, BinaryExpression> op
    ) => Expression.Lambda<Func<TSource, bool>>(op(selector.Body, value.Body), selector.Parameters);

    /// <summary><c>x =&gt; EF.Functions.Like(serializedProperties(x), pattern, "\")</c>.</summary>
    private Expression<Func<TSource, bool>> Like(string pattern)
    {
        Expression<Func<string, bool>> like = s => EF.Functions.Like(s, pattern, @"\");
        return Expression.Lambda<Func<TSource, bool>>(
            ParameterReplacer.Replace(like.Body, like.Parameters[0], serializedProperties.Body),
            serializedProperties.Parameters
        );
    }

    /// <summary>
    /// Tuple comparison <c>(time, id) &gt; (cursor.Time, cursor.Id)</c>, expressed in OR-form so
    /// EF translates it cleanly on both Postgres and SQL Server.
    /// </summary>
    private Expression<Func<TSource, bool>> After(CursorHelper.Cursor cursor)
    {
        var x = time.Parameters[0];
        var timeBody = time.Body;
        var idBody = ParameterReplacer.Replace(id.Body, id.Parameters[0], x);
        Expression<Func<DateTimeOffset>> cursorTime = () => cursor.Time;
        Expression<Func<Guid>> cursorId = () => cursor.Id;

        var body = Expression.OrElse(
            Expression.GreaterThan(timeBody, cursorTime.Body),
            Expression.AndAlso(
                Expression.Equal(timeBody, cursorTime.Body),
                Expression.GreaterThan(idBody, cursorId.Body)
            )
        );
        return Expression.Lambda<Func<TSource, bool>>(body, x);
    }

    /// <summary><c>x =&gt; new KeyedRow { Time = time(x), Id = id(x), Row = projection(x) }</c>.</summary>
    private Expression<Func<TSource, KeyedRow<TRow>>> KeyedProjection<TRow>(
        Expression<Func<TSource, TRow>> projection
    )
    {
        var x = time.Parameters[0];
        var body = Expression.MemberInit(
            Expression.New(typeof(KeyedRow<TRow>)),
            Expression.Bind(typeof(KeyedRow<TRow>).GetProperty(nameof(KeyedRow<TRow>.Time))!, time.Body),
            Expression.Bind(
                typeof(KeyedRow<TRow>).GetProperty(nameof(KeyedRow<TRow>.Id))!,
                ParameterReplacer.Replace(id.Body, id.Parameters[0], x)
            ),
            Expression.Bind(
                typeof(KeyedRow<TRow>).GetProperty(nameof(KeyedRow<TRow>.Row))!,
                ParameterReplacer.Replace(projection.Body, projection.Parameters[0], x)
            )
        );
        return Expression.Lambda<Func<TSource, KeyedRow<TRow>>>(body, x);
    }

    private sealed class KeyedRow<TRow>
    {
        public DateTimeOffset Time { get; init; }
        public Guid Id { get; init; }
        public TRow Row { get; init; } = default!;
    }

    private sealed class ParameterReplacer(ParameterExpression from, Expression to)
        : ExpressionVisitor
    {
        public static Expression Replace(
            Expression body,
            ParameterExpression from,
            Expression to
        ) => new ParameterReplacer(from, to).Visit(body);

        protected override Expression VisitParameter(ParameterExpression node) =>
            node == from ? to : base.VisitParameter(node);
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Rejecting management list request with malformed cursor (context {ContextName})."
    )]
    private static partial void LogRejectingMalformedCursor(ILogger logger, string contextName);
}
