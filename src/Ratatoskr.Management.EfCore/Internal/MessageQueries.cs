using Microsoft.EntityFrameworkCore;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Management.Contracts;

namespace Ratatoskr.Management.EfCore.Internal;

/// <summary>One inbox handler row joined to the message it belongs to.</summary>
internal sealed record InboxRow(InboxHandlerStatusEntity Status, InboxMessageEntity? Message);

/// <summary>
/// Turns a wire <see cref="MessageFilter"/> into a query, once, for both tables.
/// </summary>
/// <remarks>
/// List, count and the two matching mutations all go through here. That is the point: an operator
/// previews a filter with <c>*.count</c> and then deletes with the same filter, and those two had
/// better be selecting the same rows.
/// </remarks>
internal static class MessageQueries
{
    /// <summary>
    /// Rejects a filter that cannot be served safely, independently of which table it targets.
    /// </summary>
    public static ManagementResult? Validate(MessageFilter filter, bool requireNonEmpty)
    {
        ArgumentNullException.ThrowIfNull(filter);

        if (filter.From is { } from && filter.To is { } to && from > to)
        {
            return ManagementResult.Invalid("The 'from' bound is later than the 'to' bound.");
        }

        if (filter.IsUnboundedSearch)
        {
            return ManagementResult.Invalid(
                "A search is a substring match over serialized CloudEvents properties, so it cannot "
                    + "use an index. Narrow it with a from/to window or a status other than 'all'.",
                ManagementErrorCodes.UnboundedSearch
            );
        }

        if (requireNonEmpty && filter.IsEmpty)
        {
            return ManagementResult.Invalid(
                "A destructive operation needs a filter. Set a status, a from/to window, or a search "
                    + "term; there is deliberately no way to ask for 'everything'.",
                ManagementErrorCodes.FilterRequired
            );
        }

        return null;
    }

    /// <summary>Applies a filter to the outbox table.</summary>
    public static IQueryable<OutboxMessageEntity> Outbox(DbContext db, MessageFilter filter)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(filter);

        IQueryable<OutboxMessageEntity> query = db.Set<OutboxMessageEntity>().AsNoTracking();

        query = filter.Status switch
        {
            MessageStatusFilter.Poisoned => query.Where(x => x.IsPoisoned),
            MessageStatusFilter.Pending => query.Where(x => x.ProcessedAt == null && !x.IsPoisoned),
            MessageStatusFilter.Completed => query.Where(x => x.ProcessedAt != null),
            _ => query,
        };

        if (filter.From is { } from)
        {
            query = query.Where(x => x.CreatedAt >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(x => x.CreatedAt <= to);
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var pattern = ManagementPayloadDecoder.BuildSearchPattern(filter.Search);
            query = query.Where(x => EF.Functions.Like(x.SerializedProperties, pattern, @"\"));
        }

        return query;
    }

    /// <summary>
    /// Applies a filter to the inbox handler-status table, joining the message only when the
    /// filter needs it.
    /// </summary>
    /// <remarks>
    /// The time window and the keyset both key on the handler row's own <c>CreatedAt</c> rather
    /// than the message's <c>ReceivedAt</c>. A handler row is created when its message is accepted,
    /// so the two agree to within a transaction — and keeping the ordering key on one table is
    /// what lets the keyset scan use an index instead of sorting a join.
    /// </remarks>
    public static IQueryable<InboxHandlerStatusEntity> Inbox(DbContext db, MessageFilter filter)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(filter);

        IQueryable<InboxHandlerStatusEntity> query = db.Set<InboxHandlerStatusEntity>().AsNoTracking();

        query = filter.Status switch
        {
            MessageStatusFilter.Poisoned => query.Where(x => x.IsPoisoned),
            MessageStatusFilter.Pending => query.Where(x => x.CompletedAt == null && !x.IsPoisoned),
            MessageStatusFilter.Completed => query.Where(x => x.CompletedAt != null),
            _ => query,
        };

        if (filter.From is { } from)
        {
            query = query.Where(x => x.CreatedAt >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(x => x.CreatedAt <= to);
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var pattern = ManagementPayloadDecoder.BuildSearchPattern(filter.Search);
            var messages = db.Set<InboxMessageEntity>().AsNoTracking();
            query = query.Where(status =>
                messages.Any(message =>
                    message.Id == status.MessageId
                    && EF.Functions.Like(message.SerializedProperties, pattern, @"\")
                )
            );
        }

        return query;
    }

    /// <summary>
    /// Narrows a query to rows after <paramref name="cursor"/>, comparing
    /// <c>(CreatedAt, Id)</c> as a tuple.
    /// </summary>
    /// <remarks>
    /// Written as an OR rather than a row-value comparison so it translates on every provider we
    /// support. The <c>Id</c> half is not optional: a batch of messages poisoned by one outage
    /// shares a timestamp, and without it a page boundary inside that batch drops or repeats rows.
    /// </remarks>
    public static IQueryable<OutboxMessageEntity> After(
        IQueryable<OutboxMessageEntity> query,
        ManagementCursor? cursor
    ) =>
        cursor is { } key
            ? query.Where(x =>
                x.CreatedAt > key.CreatedAt
                || (x.CreatedAt == key.CreatedAt && x.Id > key.Id)
            )
            : query;

    /// <inheritdoc cref="After(IQueryable{OutboxMessageEntity}, ManagementCursor?)" />
    public static IQueryable<InboxHandlerStatusEntity> After(
        IQueryable<InboxHandlerStatusEntity> query,
        ManagementCursor? cursor
    ) =>
        cursor is { } key
            ? query.Where(x =>
                x.CreatedAt > key.CreatedAt
                || (x.CreatedAt == key.CreatedAt && x.Id > key.Id)
            )
            : query;
}
