using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Management.Contracts;
using Ratatoskr.Management.EfCore.Internal;

namespace Ratatoskr.Management.EfCore.Operations;

/// <summary>Returns one keyset page of inbox handler rows.</summary>
internal sealed class ListInboxOperation(
    ManagementResourceResolver resolver,
    ILogger<ListInboxOperation> logger
) : IManagementOperation
{
    public string Name => ManagementOperationNames.InboxList;

    public Type RequestType => typeof(ListMessagesRequest);

    public async Task<ManagementResult> ExecuteAsync(
        ManagementOperationContext context,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = context.RequestAs<ListMessagesRequest>();

        if (resolver.Resolve(context, DurabilityFeature.Inbox, out var db) is { } failure)
        {
            return failure;
        }

        if (MessageQueries.Validate(request.Filter, requireNonEmpty: false) is { } invalid)
        {
            return invalid;
        }

        if (!ManagementCursor.TryDecode(request.Cursor, out var cursor))
        {
            return ManagementResult.Invalid(
                "The pagination cursor is not one this service issued.",
                ManagementErrorCodes.InvalidCursor
            );
        }

        var limit = ManagementPaging.ClampPageSize(request.Limit);
        var messages = db.Set<InboxMessageEntity>().AsNoTracking();

        // A left join, not an inner one: a handler row whose message was already cleaned up is
        // still actionable, and hiding it would leave an operator staring at a poisoned count
        // they cannot reconcile with the list.
        var rows = await MessageQueries
            .After(MessageQueries.Inbox(db, request.Filter), cursor)
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .Take(limit + 1)
            .Select(status => new
            {
                status.Id,
                status.MessageId,
                status.HandlerKey,
                status.CreatedAt,
                status.CompletedAt,
                status.IsPoisoned,
                status.ErrorCount,
                status.RequeuedCount,
                status.LastError,
                Message = messages
                    .Where(message => message.Id == status.MessageId)
                    .Select(message => new
                    {
                        message.TransportName,
                        message.SerializedProperties,
                    })
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        var hasNext = rows.Count > limit;
        if (hasNext)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var items = rows.ConvertAll(row => new InboxListItem(
            row.Id,
            row.MessageId,
            row.Message is null
                ? ManagementPayloadDecoder.UnknownType
                : ManagementPayloadDecoder.ExtractType(row.Message.SerializedProperties, logger),
            row.HandlerKey,
            row.Message?.TransportName ?? ManagementPayloadDecoder.UnknownType,
            row.CreatedAt,
            row.CompletedAt,
            row.IsPoisoned,
            row.ErrorCount,
            row.RequeuedCount,
            string.IsNullOrEmpty(row.LastError) ? null : row.LastError
        ));

        var next = hasNext
            ? new ManagementCursor(rows[^1].CreatedAt, rows[^1].Id).Encode()
            : null;

        return ManagementResult.Ok(new CursorPage<InboxListItem>(items, next));
    }
}

/// <summary>Counts the inbox handler rows a filter matches.</summary>
internal sealed class CountInboxOperation(ManagementResourceResolver resolver) : IManagementOperation
{
    public string Name => ManagementOperationNames.InboxCount;

    public Type RequestType => typeof(CountMessagesRequest);

    public async Task<ManagementResult> ExecuteAsync(
        ManagementOperationContext context,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = context.RequestAs<CountMessagesRequest>();

        if (resolver.Resolve(context, DurabilityFeature.Inbox, out var db) is { } failure)
        {
            return failure;
        }

        if (MessageQueries.Validate(request.Filter, requireNonEmpty: false) is { } invalid)
        {
            return invalid;
        }

        var count = await MessageQueries.Inbox(db, request.Filter).LongCountAsync(cancellationToken);
        return ManagementResult.Ok(new MessageCountResponse(count));
    }
}

/// <summary>
/// Returns one inbox handler row, its message payload, and its sibling handlers.
/// </summary>
/// <remarks>
/// The siblings matter: the same message is usually handled by several handlers, and knowing that
/// three of four succeeded is what tells an operator whether to requeue one handler or the whole
/// message.
/// </remarks>
internal sealed class GetInboxOperation(
    ManagementResourceResolver resolver,
    ILogger<GetInboxOperation> logger
) : IManagementOperation
{
    public string Name => ManagementOperationNames.InboxGet;

    public Type RequestType => typeof(GetMessageRequest);

    public async Task<ManagementResult> ExecuteAsync(
        ManagementOperationContext context,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = context.RequestAs<GetMessageRequest>();

        if (resolver.Resolve(context, DurabilityFeature.Inbox, out var db) is { } failure)
        {
            return failure;
        }

        var status = await db.Set<InboxHandlerStatusEntity>()
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == request.Id, cancellationToken);

        if (status is null)
        {
            return ManagementResult.NotFound($"Inbox handler status '{request.Id}' was not found.");
        }

        var message = await db.Set<InboxMessageEntity>()
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == status.MessageId, cancellationToken);

        var siblings = await db.Set<InboxHandlerStatusEntity>()
            .AsNoTracking()
            .Where(x => x.MessageId == status.MessageId && x.Id != status.Id)
            .OrderBy(x => x.HandlerKey)
            .Select(x => new InboxSiblingHandler(
                x.Id,
                x.HandlerKey,
                x.IsPoisoned,
                x.CompletedAt,
                x.ErrorCount,
                string.IsNullOrEmpty(x.LastError) ? null : x.LastError
            ))
            .ToListAsync(cancellationToken);

        var properties = message?.GetProperties();
        var (json, base64) = message is null
            ? (null, string.Empty)
            : ManagementPayloadDecoder.DecodeContent(message.Content, logger);

        return ManagementResult.Ok(
            new InboxDetail(
                status.Id,
                status.MessageId,
                properties?.Type ?? ManagementPayloadDecoder.UnknownType,
                status.HandlerKey,
                message?.TransportName ?? ManagementPayloadDecoder.UnknownType,
                status.CreatedAt,
                status.CompletedAt,
                status.IsPoisoned,
                status.ErrorCount,
                status.RequeuedCount,
                string.IsNullOrEmpty(status.LastError) ? null : status.LastError,
                properties is null
                    ? new MessagePropertiesView(null, null, null, null, null, null, null, null, null)
                    : ManagementPayloadDecoder.ToView(properties),
                json,
                base64,
                siblings
            )
        );
    }
}
